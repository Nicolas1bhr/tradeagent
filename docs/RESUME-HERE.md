# Resume here

**Read this first, then `BUILD-STATUS.md`.** This file says where the work stopped, what to do next,
and which traps have already been paid for. `BUILD-STATUS.md` says what is proven and what is not,
with the evidence quoted; it is the honest record and it is kept that way deliberately.

Short on purpose. A handoff nobody can afford to read is not a handoff.

---

## Do this first

**The machine needs a rendering surface before anything else can happen.** ATAS is DOWN and will not
restart without one: it signs in, opens its main window, and dies ~40 s later in
`glfwGetVideoMode` while building its OpenGL chart panels (trap 43). That is a property of a
disconnected RDP session, not a fault — and it is new knowledge, because ATAS had been *running*
across the disconnect for days, which it tolerates perfectly well.

**So step one is: reconnect the RDP session, or reboot into the console (autologon is on).** Then
launch ATAS, sign in, and re-activate the bridge strategy on the BTCUSDT chart — it always comes back
stopped (traps 24 and 40). Only then is the machine in the state the rest of this file assumes.

**Then take the reading, because everything else for it is already in place.** The instrument for
"does `OpenOrderAsync` complete on SUBMISSION or on broker ACKNOWLEDGEMENT?" is written, compiles
against the real SDK, and **is already deployed in `%APPDATA%\ATAS\Strategies` and asserted
present**. It has simply never placed an order. One probe run takes it:

```bash
tools/win-run.sh 'taskkill /IM TradeAgent.exe /F'
tools/win-run.sh 'cd C:\ta\repo\tools\probe && dotnet run -c Release -- atas --place-test-order --yes'
```

Read `PLACE TIMING` and `ACK LATENCY` in its output; the probe prints the verdict itself, including
`NOT SEPARABLE`, which is a real possible answer and must not be rounded to a green. Full reasoning
in **The work queue**, task 1. **Cancel anything left resting and verify from a separate run.**

Confirm the machine first — two commands, and neither disturbs anything:

```bash
tools/win-state.sh
tools/win-run.sh '"%LOCALAPPDATA%\TradeAgent\bin\trade.exe" status'
```

Four facts that otherwise cost the first twenty minutes:

- **`probe atas` needs TradeAgent STOPPED.** Both open a server on the bridge pipe.
- **Stop TradeAgent before any push** (trap 42) — and then **rebuild it**, because the push deletes
  `C:\ta\repo\src`, which is where its Release build lives:
  `dotnet build src\TradeAgent.App\TradeAgent.App.csproj -c Release`.
- **Do not close ATAS unless you have a rendering surface** (trap 43). Closing it is the step that
  cannot be undone remotely.
- **Screen capture does not work; automation does.** A rendering surface fixes both.

Everything below is reference. Read **The work queue**, then the traps for whatever you are about to
touch.

---

## What is settled — carry these, do not re-prove them

**`LIVE_CONFIRM` is walked, through ATAS, on hardware.** An AI session proposed
`buy 1 BTCUSDT limit 70000`, the gateway refused it with `APPROVAL_REQUIRED` and parked it, a human
approved it in the app (two presses), and it reached ATAS as broker order `12021602` before being
cancelled and the book verified clean. That was the reachable milestone and it is done.

**Rule 1 is proven, across a process restart.** An order was placed, ATAS was shut down, and the
identifier was found again on an order in the restarted platform's own collection, beside the broker
id the dead run had recorded in advance.

```
BRIDGE SESSION : 1ce7ec65        RECORD SESSION : bccb57cf
ORDER SURVIVED : YES — broker id 12007918
IDENTIFIER     : YES — client_order_id = TA-PROBE-20260830120255
RULE 1         : PROVEN ACROSS A PROCESS RESTART. THIS IS THE ANSWER.
```

The process that read it had constructed no `Order` at all, so the match cannot be our own object —
which is exactly what made every earlier reading worthless. **In-session the reading is still
`proven-sameref` and still reports false**, on two different connectors, so that is how ATAS's
collection works rather than one backend's quirk. **The bound is real and printed in the verdict:** a
cross-session match cannot separate ATAS rebuilding the order from *the broker's* answer on
reconnect, from ATAS rehydrating it out of its own local store. The identifier survives ATAS
restarting, which is what reconciliation needs. Whether it ever reached the broker is a different
question, and only the broker's own report answers it.

**An account nobody chose could be traded, and now cannot.** `PlaceAsync` resolves its account
through a helper that falls back to the platform's first account when none has been chosen —
harmless for rendering a status screen, and it reached the broker. `TryAuthorizeExecution` now
refuses with `ACCOUNT_NOT_FOUND`, and the emergency controls sit outside that gate on purpose. The
shape worth carrying: it was unreachable until the same session made the platform changeable after
setup, so **the feature did not cause the hole, it revealed one — and the fix belonged in the
gateway, not in the page.**

**Where the two autonomy gates stand:**

- `SupportsClientOrderId` — **true, on evidence**, since 2026-08-30.
- `SupportsOrderHistory` — **false, for a known reason.** `GetService<T>()` throws
  `NotSupportedException` for *every* type, including one reachable as a property on the same
  interface, so no cache route exists. That is an answer, and a shippable one.

`ReconciliationProvable` is false and the gateway refuses `LIVE_AUTONOMOUS`. **That is still
correct:** one gate is open on evidence, the other is shut on an answer. Do not "fix" the second by
hard-coding it.

## The rule that shapes every design decision

**A terminal is never shown to the user. Not once.** It is the entire reason this product exists:
the underlying capability is already available to anyone willing to use a shell, and what is being
sold is that nobody has to. This rule quietly forbids the obvious implementation of several
features, so before "just shell out to it" feels reasonable, read
`docs/DECISIONS.md` and the class comment on `AtasPrerequisite`.

The rule also *creates* bugs that only exist because of it — see trap 1 below.

## Where the machine is (2026-08-31, end of the later session)

**ATAS is DOWN and cannot be restarted until the session has a rendering surface (trap 43).**
TradeAgent is up and reporting that honestly. Nothing can trade; nothing is at risk.

```
session : Disc (id 1)   desktop : renders nothing — automation WORKS, capture does NOT, ATAS CANNOT START
ATAS       : NOT RUNNING. Closed deliberately to swap the bridge DLL; two relaunches crashed in
             glfwGetVideoMode ~40s after sign-in. The workspace was SAVED on the way down.
bridge DLL : CURRENT and deployed — carries the place-timing instrument, asserted present in the
             deployed file (OrderShape, _lastPlace, AtasStrategyAdapter as ASCII metadata)
TradeAgent : Release, rebuilt from this tree after the push, running
mode : LIVE_CONFIRM   live_activated : FALSE   execution : blocked, twice over
book : orders [] · position 0 · nothing unreconciled  (verified before ATAS was closed)
health : ATAS process DEGRADED "not running", ATAS bridge FAILED "installed — waiting for ATAS to start"
```

**To get back to a working machine:** restore a rendering surface, launch ATAS, sign in, then
re-activate the bridge strategy on the BTCUSDT chart — the workspace was saved, so it will be listed
under "Selected strategies", **stopped** (trap 24). `find --query 'Activ'` returns
`PART_ActivateButton` directly and `invoke --ref` starts it (trap 40). **Check the Selected list
before pressing Add**, or two bridges end up on one pipe (trap 35).

**The workspace layout, and it is load-bearing:**

- **The bridge belongs on the BTCUSDT chart** (left panel), on the simulated `CRYPTO5EB41` account.
  **The ES chart has no strategy at all** — deleted deliberately, because two instances compete for
  one named pipe. Keep it that way.
- **Why crypto:** ES is a CME product, so out of hours `quote=none(no-tick)` and the probe correctly
  refuses to price an order. BTCUSDT runs 24/7 on Binance (trap 38).
- **Both accounts are simulated:** `CRYPTO5EB41` (USDT, Binance crypto-sim) and `DEMO15M440CE` (USD,
  ATAS Sim), 100,000 balance each.
- **The witness file holds the rule-1 proof:** `witness=session:1ce7ec65,records:1,prior:1,io:ok`
  with `coid=proven-crosssession`.
- **The ATAS account list offers exactly one account** — the portfolio the bridge's chart is bound
  to — because `ChartStrategy.Connector` is null (trap 13). Changing ATAS account means moving the
  strategy to a chart on the other account, not picking a different row in Settings.

**The connector is `atas` and the account `CRYPTO5EB41`. Both are now changeable in the app** — there
is a Settings page, so the database no longer has to be edited by hand.

The database is at schema 2, migrated in place from this machine's own schema-1 database. A copy of
the pre-migration file is at `%LOCALAPPDATA%\TradeAgent\state\tradeagent.db.pre-schema2`.

## New in scope since 2026-08-31: the AI inbox

The owner can hand the agent programs, documents and data to experiment with. Built on the Mac,
green, screenshotted — **and never touched on Windows, never dragged onto with a real mouse, and
never used by an actual agent.** `BUILD-STATUS.md`'s 2026-08-31 section is precise about which is
which; the short version is that the storage and the wire are tested and the *interaction* is not.

What exists: `workspace/inbox/`, an **Inbox** page in the shell, a bounded scanner, a two-table
ledger (schema 2), and `trade material list|ran|used|derived|note` on the agent channel.

**The rule that shaped it, and that must not be softened:** what is measured and what is claimed are
stored in different tables. `material` rows come from a directory listing and a hash the software
computed; the agent cannot write or edit one. `material_note` rows are the agent's own account of
itself. Merging them, or letting a note touch a material row, turns a record into an assertion —
which is the entire failure this was built to prevent.

What is left on it is in **The work queue** — dragging a real file onto it is part of task 2, and
watching an agent actually use it and the size cap are in task 4. It is listed there rather than here
so there is one queue rather than two.

## The work queue

### 1. Measure `OpenOrderAsync`, then decide about the four call sites — THE NEXT TASK

**The question.** `ITradingManager.OpenOrderAsync(order, setDefaultQuantity, askConfirmation,
checkOrderStates)` exists with the same four flags as the synchronous overload the adapter calls
today — `docs/atas-api-8.0.14.397.txt`, `interface ATAS.Indicators.ITradingManager` at line 1391,
the async overload at 1421 directly under the obsolete synchronous one at 1420, same four flags.
**Does the Task it returns complete when ATAS has submitted the order, or only when the broker has
acknowledged it?**

**Why it decides anything.** Read the long comment on `AtasCall.Block` before starting — it already
records the reasoning and it corrects an earlier version of this file that was wrong. In short:

- It is **not** about rule 3's classification. There is no `catch` anywhere in the adapter's write
  path; every `AtasRejectedException` after submission is manufactured from `_failures`, fed by
  ATAS's `OrderRegisterFailed` events. The sync/async choice does not touch that path at all.
- It **is** about deadlines. `AtasCall.Block` can put a deadline on a Task. It cannot put one on a
  synchronous call. Today four of the five write paths — `OpenOrder`, `ModifyOrder`, `CancelOrder`,
  `ClosePosition` — have **no deadline at all**, so a block in any of them stops `BridgeServer`'s
  frame loop, including the operator's cancel-all, while the heartbeat goes on reporting READY
  (trap 31).

**The numbers, so the risk is arithmetic rather than a feeling.** `AckTimeout` 3s, `CallTimeout` 5s,
worst case a caller waits 8s; `AtasConnector`'s RPC timeout is 10s. So the switch trades *"a wedged
pipe nobody can interrupt"* for *"every slow order becomes UNKNOWN at 5s"*. Which of those you get
depends entirely on the answer above, which is why it is measured before anything is flipped.

**How to measure it.** At the instant the Task completes, capture three things and print them:
elapsed ms, whether `order.Id` is assigned, and `order.State`. Then keep watching and print elapsed
to Id-assignment and to the first state change.

- Task completes with `Id` empty and `State == None`, clearly before the Id appears → **SUBMISSION**.
- Task completes only once `Id` is assigned or `State` has moved → **ACKNOWLEDGEMENT**.

**The trap in this measurement, and it is the whole difficulty.** The Binance crypto sim may
acknowledge in under a millisecond, in which case submission and acknowledgement are *not separable*
and a fast completion is NOT evidence for submission. Design the instrument to say
`not-separable` rather than to produce a false green — the same discipline as `proven-sameref` and
the `GetService` control probe. **Place a control order through the existing synchronous path in the
same run and print both timelines side by side**; the sync call is known-good behaviour, so a
difference between them is the reading, and no difference on a sim that answers instantly is an
honest "this platform cannot separate them here".

**Where to put it.** A probe-only route, not a second branch in the live write path — a second way to
place an order inside `Place` is exactly where a misclassification would hide. Keep the probe's
existing refusal to place unless the account is provably simulated from two independent sources;
there is deliberately no `--force` and no `--account`, and that stays.

**The physical loop.** Every line below is copied from one that has run; the detail and the
corrections are under "Driving ATAS from here", which is the section to read before doing it.

```bash
tools/win-run.sh 'taskkill /IM TradeAgent.exe /F'     # frees the bridge pipe AND unblocks the push
tools/win-push.sh
# close ATAS SAVING the workspace — it holds the bridge DLL
tools/win-run.sh 'cd C:\ta\repo && dotnet build src\TradeAgent.AtasBridge\TradeAgent.AtasBridge.csproj -c Release -p:AtasBridgeBuild=true -p:AtasInstallDir="C:\Program Files (x86)\ATAS Platform"'
# copy TradeAgent.* into %APPDATA%\ATAS\Strategies, then assert the DEPLOYED dll (trap 8, trap 27)
# relaunch ATAS, sign in, RE-ACTIVATE the strategy — it always comes back stopped (trap 24, trap 40)
tools/win-run.sh 'cd C:\ta\repo\tools\probe && dotnet run -c Release -- atas --wait 60'
```

**Traps that apply to this specific task:** 12 (a bridge built without `AtasBridgeBuild=true` is a
stub that looks identical), 8 and 27 (assert the deployed DLL, names as ASCII and literals as
UTF-16), 34 (**add no NuGet package anywhere in the bridge's chain** — the deploy filter drops
third-party files silently), 24 and 40 (the strategy returns stopped; `PART_ActivateButton` is real
and does not need the row expanded), 35 ("No selected strategy" is the right-hand pane's placeholder,
not an empty list — check before pressing Add or you get two bridges on one pipe), 39 (never pass
`--window` to `find`).

**Whatever the reading, always:** cancel anything left resting and verify the book from a *separate*
run. `probe atas --cancel-resting <client-order-id>`, then `trade orders` should print `[]`.

**What to do with each answer.**

- SUBMISSION → flip the four call sites to the Async overloads wrapped in `AtasCall.Block(...,
  CallTimeout, "<name>")`, as its own commit, and say in `BUILD-STATUS.md` what was measured.
- ACKNOWLEDGEMENT → **do not flip.** Either raise `CallTimeout` above the observed ack latency and
  the connector's 10s with it, or leave the synchronous calls and record that the four undeadlined
  write paths are a known, measured limitation. Both are honest; guessing is not.
- NOT SEPARABLE on this platform → record that, and leave the call sites alone. An unanswerable
  question answered "no" is how the `SupportsOrderHistory` mistake would have been made.

### 2. Look at TradeAgent's own UI on Windows — with eyes

Partly started: on 2026-08-31 the **Settings** page was read there through UI Automation (its labels
come back, and the two buttons that should be disabled are), which proves it renders and holds the
right state. That is not the same as seeing it. The RDP session renders nothing (trap 19), so
**reconnect or unlock the console first** or captures come back useless.

Three things specifically want eyes, and none of them can be settled by automation:

- the setup journey end to end;
- the **Inbox** page — drag a real file onto it, which has never been done on any platform. The drop
  handler, the file picker, the copy, the collision suffix and the immediate rescan are all compiled
  and unexercised;
- the bridge-refusal sentence on the dashboard status row. It is ~450 characters and got longer when
  the `witness=` token was added. No truncation was found in the UI, but nobody has watched it render.

### 3. The staged live trial

paper → extended paper run → one tiny live order → disconnect/recovery test → autonomous live
permission. `LIVE_AUTONOMOUS` is still refused and correctly so: `ReconciliationProvable` needs
`SupportsOrderHistory`, which is false because `GetService<T>()` throws for every type. Rule 1 opened
one gate; the other is shut on an answer, not a gap.

### 4. Small, real, and deliberately not smuggled into anything else

- **`Agent runtime`, `Agent process` and `Workspace` are blank `UNKNOWN` rows** until the AI is
  started, so a simulator user reads "3 parts not checked yet" in the rail forever. Unlike the ATAS
  rows these *do* get written eventually — a wording problem, not a missing writer.
- **The rail counts a deliberately-not-in-use row as "not checked yet".** Doing it properly wants a
  `NOT_APPLICABLE` health state, which touches `Doctor.AllHealthy` (it would otherwise never report
  all-clear on the simulator) and the `trade status` wire. Contained, but its own piece of work.
- **Is `inbox/` size-capped?** Nothing stops a 60 GB drop. The scanner survives it — hashing is
  bounded per pass — the disk may not.
- **Watch a real agent use the inbox.** Drop something in, ask the AI to work with it, and see
  whether it runs `trade material ran ...` unprompted. If it does not, the notes half of the ledger
  is empty forever and `AGENTS.md`'s wording is what needs fixing, not the code.

## Do not redo any of this

A scan list. The detail is in `BUILD-STATUS.md` under each date, and the headline facts are under
"What is settled" above.

**2026-08-31, later session**
- The two ATAS health rows are written every health tick. They had *no writer at all* — that was the
  cause under the symptom the earlier session recorded.
- There is a **Settings** page: platform and account are changeable after setup.
- `TryAuthorizeExecution` refuses when no account has been chosen. **Do not "simplify" that gate
  away, and do not pull the emergency controls inside it** — they are outside `AuthorizeOrThrow` on
  purpose, and a test pins that they are.
- `ITradingManager.Orders` and `ChartStrategy.Orders` are not the same collection.
- `win-push.sh` refuses to run while anything is running out of `C:\ta\repo` (trap 42).

**2026-08-31, earlier**
- `LIVE_CONFIRM` walked end to end through ATAS, broker order `12021602`.
  *Still to do on that path:* a **filling** order (that one rested and was cancelled), a **decline**,
  and the same walk against a real broker.
- The AI inbox exists: `workspace/inbox/`, the Inbox page, the scanner, the two-table ledger,
  and `trade material list|ran|used|derived|note`. Storage and wire tested; interaction is not.

**2026-08-29 and 2026-08-30**
- Rule 1 proven across a process restart.
- The bridge pipe authenticates in both directions; an unproved peer cannot unlock autonomy through
  a hello, a heartbeat or an event.
- A same-reference match no longer reports the capability true, and an object the adapter built
  cannot pass for one ATAS built.
- ATAS calls and bridge teardown have deadlines.
- The whole adapter compiles and runs against real ATAS, and the probe explains a refusal instead of
  timing out.

## Reading TradeAgent from here, when capture is unavailable

The RDP session renders nothing most of the time (trap 19), and for a whole session that read as
"the UI cannot be checked". It can. Two routes, both used on 2026-08-31 and both better evidence than
a screenshot anyway, because they produce quotable data rather than a picture somebody has to read.

**The app answers its own pipe.** `GatewayStatus` carries the whole health snapshot, so the trade CLI
prints every row the dashboard shows:

```bash
tools/win-run.sh '"%LOCALAPPDATA%\TradeAgent\bin\trade.exe" status'   # mode, account, health[], risk
tools/win-run.sh '"%LOCALAPPDATA%\TradeAgent\bin\trade.exe" orders'   # what is actually working
tools/win-run.sh '"%LOCALAPPDATA%\TradeAgent\bin\trade.exe" positions'
```

This is how the ATAS health rows were proved: the same command, before and after the fix, on the same
machine and the same ATAS session.

**UI Automation reads the tree without rendering it.** `find` returns names, types and — the useful
part — `enabled`, which is where most state actually lives:

```bash
tools/win-ui.sh find --query 'Account in use'     # never pass --window (trap 39)
tools/win-ui.sh find --query 'Use ATAS'           # enabled=False means "already the platform in use"
tools/win-ui.sh invoke --ref <the nav button>     # switch pages, then read again
```

What neither route gives you is whether it *looks* right — colour, spacing, truncation, a label
running off the end of a row. That still needs a reconnected session. Do not confuse "the state is
correct" with "the screen is correct"; the ~450-character bridge-refusal sentence in work-queue
task 2 is exactly the kind of thing only eyes will catch.

## Driving ATAS from here

Re-performed end to end on 2026-08-30, unattended, and **corrected where the 2026-08-28 version was
wrong**. Every line below is a command that ran.

```bash
tools/win-state.sh                             # interactive=True, or nothing below works
tools/win-ui.sh launch --path 'C:\Program Files (x86)\ATAS Platform\OFT.Platform.exe'
tools/win-ui.sh find --window 'Authorization' --query Connect   # credentials are saved
tools/win-ui.sh invoke --ref <ConnectButton>                    # ~30s to the main window
tools/win-ui.sh click --x 768 --y 768 --button right            # empty space in the LEFT chart
tools/win-ui.sh find --query 'Chart strategies'                 # take the Button hit, then invoke
tools/win-ui.sh find --query 'TradeAgent Bridge'                # TreeItem in AVAILABLE strategies
tools/win-ui.sh select --ref <that TreeItem>                    # this is what ENABLES Add
tools/win-ui.sh find --query 'Add' --type Button                # confirm enabled=true now
tools/win-ui.sh invoke --ref <Add>
tools/win-ui.sh find --query 'Activ'                            # PART_ActivateButton — it IS there
tools/win-ui.sh invoke --ref <PART_ActivateButton>              # ▶ becomes ■; legend goes [Started]
tools/win-ui.sh find --query 'DialogButton'                     # the Cyrillic ОК — trap 16
tools/win-ui.sh invoke --ref <PART_ОКDialogButton>
```

**Three corrections to the older recipe, each of which cost time on 2026-08-30:**

- `--x 768 --y 768` is a **screen** coordinate. The old `770,607` was read off a window-relative
  screenshot and lands on the Windows desktop. See trap 37.
- **CORRECTED 2026-08-31: there IS a `PART_ActivateButton` step, and the coordinate click is not
  needed.** With the dialog open, `find --query 'Activ'` returns it, enabled, without expanding the
  row; `invoke --ref` starts the strategy and the row icon goes ▶ → ■. The 2026-08-30 claims that it
  does not exist and that `Activ` finds nothing were both wrong. Prefer the named element — a raw
  coordinate inside a modal is trap 37 waiting to happen.
- **Never pass `--window` to `find`.** For the Chart strategies dialog it answers `no visible window
  matching`; for the ATAS `Authorization` window it **killed the UI agent outright** (trap 39).
  Search globally, always.

**Read the state off the chart legend, not the icon.** The legend says `[Started]` or `[Stopped]` in
words. `Selected strategies` on the left is the list; `No selected strategy` on the RIGHT is the
settings pane's placeholder and does **not** mean the list is empty — believing it is how you end up
with two bridges on one pipe (trap 35). If the toggle will not co-operate, `Delete` the row and
re-add: that button behaves predictably.

**Closing ATAS**, and it must save or the strategy does not come back at all:

```bash
tools/win-ui.sh windows                                  # find the main hwnd (isMain=true)
tools/win-ui.sh close --hwnd <that>                      # WM_CLOSE; reports stillOpen=true, expected
tools/win-ui.sh find --query 'Save and close'            # the modal it raises; take the Button hit
tools/win-ui.sh invoke --ref <that>
# then poll: tasklist /FI "IMAGENAME eq OFT.Platform.exe"  — it takes ~10-15s to actually exit
```

**Rebuilding and redeploying the bridge** (ATAS must be closed — it holds the DLL):

```bash
tools/win-push.sh
tools/win-run.sh 'cd C:\ta\repo && dotnet build src\TradeAgent.AtasBridge\TradeAgent.AtasBridge.csproj -c Release -p:AtasBridgeBuild=true -p:AtasInstallDir="C:\Program Files (x86)\ATAS Platform"'
# then copy TradeAgent.* from bin\Release\net10.0-windows into %APPDATA%\ATAS\Strategies
# and assert the DEPLOYED dll, not the built one (trap 8), names as ASCII / literals as UTF-16 (trap 27)
```

**The rule-1 restart experiment**, which is how `coid=proven-crosssession` was obtained and is
re-runnable whenever the mechanism needs re-proving:

```bash
probe atas --place-test-order --yes --leave-resting --yes-leave-it   # exits 5, prints the removal command
#   ... close ATAS SAVING the workspace, relaunch, sign in, re-activate the strategy ...
probe atas --coid-restart-check                                      # proof | disproof | not-answered
probe atas --cancel-resting <client-order-id>                        # ALWAYS, and verify from a separate run
```

Half 2 places nothing, deliberately: the cross-session branch requires the id to be **absent** from
`_submitted`, so a second half that placed anything would destroy the reading it exists to take. It
also refuses when the bridge session equals the record session — i.e. when ATAS was never actually
restarted, which is the way to fool yourself here.

**Read the UI, do not click at it.** `find` and `tree` return named elements; `invoke --ref` acts on
the one you looked at. The chart's context menu has `Sell Limit at ...` and `Buy Stop at ...` three
rows above `Chart strategies`, and the trading panel has four `Add`-ish buttons — coordinates would
eventually hit one of them. The two coordinates above are the exceptions: one into empty chart space,
one onto a list-row toggle inside a modal dialog.

## Open questions nobody has answered

- ~~**Does ATAS carry our client order id onto anything we did not write?**~~ **Answered
  2026-08-30: yes, across a process restart.** An order was placed, ATAS was shut down and confirmed
  gone, and the identifier was found again on an order in the restarted platform's collection beside
  the broker id the dead run had recorded in advance. The reading process had constructed no `Order`
  at all, so the match cannot be its own object.
  **The bound is the open part:** a cross-session match cannot separate ATAS rebuilding the order
  from *the broker's* answer on reconnect from ATAS rehydrating it out of its own local store. The
  identifier survives ATAS restarting — which is what reconciliation needs. **Whether it ever reached
  the broker is still unanswered**, and only the broker's own report answers it.
- **Does `OpenOrderAsync`'s task complete on SUBMISSION or on broker ACKNOWLEDGEMENT?** The gate on
  flipping the four obsolete order calls, and unanswerable off Windows. If acknowledgement, blocking on
  it puts `Place` past the connector's 10s RPC timeout and turns every order into UNKNOWN.
- ~~**Are `ITradingManager.Orders` and `ChartStrategy.Orders` the same list?**~~ **Answered
  2026-08-31, out of the captures already on the machine: NO, they are not the same collection.**
  With one resting order live in ATAS, `probe-half2.txt`, `probe-clean.txt` all report
  `orders=1 strategyorders=0`, and `probe-verify.txt` reports `orders=0 strategyorders=0` after the
  cancel — so the 1 was tracking the real order. Both counts are built inside one `SurfaceReport`
  call, i.e. from a single instant, and a shared list cannot report two lengths at once.
  **The narrower half is still open and the captures cannot answer it:** every `trading_surface`
  reading ever taken was at the hello, *before* anything was placed, so `strategyorders=0` has never
  been observed in the one situation that would give it meaning — an order this strategy instance
  placed in this session. The probe now takes the reading again after the place and prints
  `ORDER COLLECTIONS   before: ... after: ...`, so the next run on hardware closes it.
  Either way `LiveOrders`' de-duplication stays: it is defensive rather than load-bearing on the
  evidence, it costs one `HashSet`, and what it prevents is `FilledOf` double-counting a partial
  fill into a FILLED.
- **Does ATAS's order collection ever contain `Modify`'s cloned replacement?** Unanswerable from the
  API dump, which carries public members only. It decides whether trap 32 is live or merely possible;
  the guard against it does not depend on the answer, and must not be "simplified" until it is known.
- ~~**Is any order-history cache reachable?**~~ **Answered: no.** `GetService<T>()` throws
  `NotSupportedException` for every type, including one reachable as a property on the same interface.
  The control probe is what makes that an answer rather than "try another type".
- **Do the order calls work off the GUI thread?** The synchronous ones do — now several live data
  points across 2026-08-28 and 2026-08-30, including a place, a read-back and two cancels from the
  bridge's pipe thread. The Async ones have never been called.
  Note the "under two seconds" figure previously recorded here **is not quotable from any instrument
  in this repository**: nothing times the place call, and the probe's only `Stopwatch` on that path
  times the read-back. What the run proves is that `Place` returned inside the connector's 10s RPC
  timeout without a rejection.
- **What is the sign convention on `Position.Volume`?** Deliberately unused: getting it wrong would
  not flatten a position, it would double it, so `ClosePosition` lets ATAS pick the side instead.
- **Does Windows Defender Firewall prompt when `codex login` binds its callback socket on port
  1455?** A prompt there lands in front of a user the product promised would click Yes exactly once.

## Traps already paid for

Each of these cost real time. None is obvious from the code.

1. **A spawned AI CLI hangs forever unless you close its stdin.** `codex exec` reads stdin *in
   addition to* the prompt argument and waits for end-of-file; a windowless GUI app's inherited
   stdin never ends. The turn sits at `Busy = true` indefinitely, which is **indistinguishable from
   the AI thinking**, so nothing ever reports an error. Both `AgentSession` and
   `CliAgentRuntime.Run` redirect stdin and close it immediately. Do not "simplify" that away.
2. **A GUI program started over SSH has no desktop.** Screenshots come back black, clicks go
   nowhere. Go through `tools/win-shot.sh`, which uses a scheduled task with `LogonType Interactive`
   — and the desktop must be **unlocked**, or captures are blank white and read as a broken app.
3. **macOS `tar` smuggles AppleDouble `._*` files into the archive** and `csc` rejects every one as
   "a binary file instead of a text file". `tools/win-push.sh` sets `COPYFILE_DISABLE=1`.
4. **Fluent paints its templates' inner borders from nested styles**, so `TextBox.Background` and
   `Button.Background` silently do nothing in states you have not overridden. Every state needs an
   explicit rule in `Theme.cs` — resting, hover, pressed, focus **and disabled**. Two separate
   defects came from this one cause.
5. **`Theme` collides with `StyledElement.Theme`** inside any `Control` subclass, so the bare name
   resolves to the inherited property. `MainWindow.cs` carries `using Tokens = TradeAgent.App.Theme;`
   for exactly this reason.
6. **`dotnet` is not on PATH on the dev Mac** (it lives at `~/.dotnet`), and running the built
   apphost directly also needs `DOTNET_ROOT`. `tools/mac-run.sh` handles both.
7. **ATAS's own documentation renders `%APPDATA%` as `APPDATA%`** (a Doxygen escape artefact), and a
   superseded ATAS blog post gives a `Documents\ATAS\...` path that no current doc mentions. The live
   paths are `%APPDATA%\ATAS\Strategies` and `%APPDATA%\ATAS\Indicators` — **different folders**, and
   a strategy DLL in the indicators folder is silently ignored.
8. **A green build is not proof the change reached the machine.** A `scp` that reported "Connection
   closed" still produced a successful-looking remote build — of the *previous* source. Assert the
   artifact's identity (grep the remote file for the new symbol) before believing the build.
9. **A capability that is true from the first frame never has to travel, so no test exercises the
   frame that carries it.** `LoopbackAtasAdapter` reports `SupportsClientOrderId = true` at the
   handshake, so every bridge test passed while the real adapter — which turns it true only *after*
   an order proves it — could never get that answer across at all. When a test double answers
   immediately, it is not testing the thing that makes the real one hard.
10. **`LogonUI` is not a lock indicator on a machine reached over RDP.** Windows runs one in the
    physical console session whenever that console sits at the lock screen, which is permanently.
    `tools/win-state.sh` reported a live remote desktop as locked because of it. Match the process to
    the *session*. The same split means `win-shot.sh` cannot photograph an RDP desktop — its
    scheduled task lands on the console, a different desktop, and captures blank.
11. **Four layers of quoting sit between a shell here and PowerShell there**, and the symptom of
    getting it wrong is *empty output*, not an error — which reads as "the machine did not answer".
    Use `tools/win-ps.sh`, which base64-encodes the script as UTF-16LE and hands it to
    `-EncodedCommand`.

12. **A bridge DLL built without `-p:AtasBridgeBuild=true` is a stub, and it looks exactly like the
    real one.** Same filename, same folder, loads without complaint — and contains no
    `ChartStrategy` subclass at all, so ATAS lists nothing and says nothing. The visible symptom is
    "TradeAgent Bridge is not in the Strategies list", which is *identical* to the symptom of trap 1
    (ATAS not watching the folder), and trap 1's fix — press refresh — is the first thing anyone
    tries. It cannot work, and there is no message anywhere to say so. This is deliberate on the
    build's part: `packaging/build.ps1` will not pretend to ATAS support it cannot have. The trap is
    that the *installed* app can be an older, ATAS-less build while the repo builds a real one.
    Check the DLL for the type name `AtasStrategyAdapter`, not the file's existence — and note trap 27:
    check type names as ASCII, string literals as UTF-16, or a correct build reads as absent.

13. **`ChartStrategy.Connector` is null, and nothing says so until runtime.** It exists, it has the
    right type, the code compiles, and every read through it throws "this ATAS chart has no trading
    connection attached yet" on a chart that is demonstrably attached to a portfolio. `Portfolio` on
    the same object is populated. The trading surface for a chart strategy is `ITradingManager`.
    This cost the first live run, and it is why the rule-1 question was asked of
    `ITradingManager` rather than of the connector.

14. **A DPI-unaware process is handed virtualised coordinates, and every click lands near its
    target instead of on it.** On this machine `GetWindowRect` reported 2208x1533 for a window DWM
    described as 1530x914. Mixing the two spaces fails intermittently and reads as a flaky UI.
    `winagent` calls `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` as its very first act,
    before anything reads a coordinate. Do not move that line.

15. **cmd.exe is what is on the far end of ssh, and `;` is not a command separator there.** It is
    swallowed as an argument. `win-agent.sh stop` ran `schtasks /end ...; taskkill ...` and taskkill
    silently never ran, so "stop" stopped nothing and the next build failed on a locked file.

16. **ATAS labels its dialog buttons with Cyrillic "ОК"** (U+041E U+041A), which renders identically
    to Latin "OK". A search for `OK` finds *nothing at all* — not the wrong element, no element. The
    automation ids look the same too: `PART_ОКDialogButton`. Search by automation id, or paste the
    string out of a `tree` dump rather than typing it.

17. **The UI agent holds its own executable open**, so a rebuild fails with MSB3027 "the file is
    locked by winagent" and names the process rather than the cause. `win-agent.sh build` now stops
    it, builds, restarts it and prints status.

18. **The Windows box refuses roughly one SSH connection in ten under rapid use** —
    `Permission denied (publickey,password,keyboard-interactive)` on a connection that worked a
    second earlier. `win-ui.sh` retries that one message and only that one: authentication happens
    before any request is written, so nothing was actuated and the retry cannot double-press a
    button. **Do not widen that retry** to cover timeouts or transport errors; those can mean the
    click already landed.

19. **A disconnected RDP session keeps automation and loses only rendering.** UI Automation, element
    invokes and the ATAS bridge all keep working; `CopyFromScreen` throws "The handle is invalid".
    The agent used to report one `can_drive_ui: true` for both and was therefore lying in the only
    case where it mattered; it now reports `can_automate` and `can_capture` separately, and settles
    the second by attempting a one-pixel grab rather than reasoning about it.

20. **A `.ps1` written without a byte-order mark is read as ANSI by Windows PowerShell**, so every
    non-ASCII character in it — an em dash, a curly quote — arrives mangled and can break parsing
    outright. The error is `The string is missing the terminator` pointing at a line that is
    perfectly correct, which sends you hunting an unbalanced quote that does not exist. This only
    affects `win-ps.sh`'s **long-script** branch (the `-EncodedCommand` branch declares UTF-16LE and
    is immune), which is exactly why the branch passed its first verification: that test was pure
    ASCII. `win-ps.sh` now writes the BOM.

21. **A push silently half-deletes anything running out of the repo, and nothing fails until the
    next reboot.** `win-push.sh` clears `C:\ta\repo\tools` before unpacking. The UI agent ran from
    there. Windows refuses to delete a RUNNING `.exe`, so `winagent.exe` and `winagent.dll` survived
    — but `winagent.runtimeconfig.json` was not locked by anything and was deleted, under `-EA 0`,
    so the push reported success. The already-loaded agent kept working perfectly for hours. Only at
    the next reboot did the apphost find no runtimeconfig and the logon task die with
    **`0x80008083` (CoreHostLibMissingFailure)** — a bare hex number in Task Scheduler that nothing
    connects back to a push. The machine came up with autologon working, the desktop live, and no
    way to drive it. The agent now runs from `C:\ta\agent\bin`; the push reports what it could not
    delete instead of hiding it.

22. **`windows` showed one window per process, so a modal dialog was invisible — and a modal was the
    answer every time.** ATAS was asked to close three ways (UIA `Invoke` on `PART_CloseButton`, a
    physical click on it, ALT+F4) and stayed running each time. **None of them was ignored.** Each
    raised a `Save current workspace?` modal that the tool could not see, and ATAS sat waiting for an
    answer. With screen capture unavailable at the time, UI Automation was the only sense available
    and it was blind in exactly the place it mattered. `windows` now enumerates every top-level
    window with `owner`, `isMain`, `enabled` and `class`, so the modal signature — **a disabled main
    window plus an enabled window it owns** — is readable at a glance.

23. **Activating a chart strategy raises a modal, and the Start button does not exist until you
    expand the row.** Two separate traps in one dialog:
    - The `IsActivated` checkbox in the strategy settings grid **cannot be toggled**. Clicking it,
      clicking it twice and pressing Space all leave it unchecked, because `ChartStrategy.IsActivated`
      is `{ get; }` in the ATAS API — the grid is displaying state, not offering a control.
    - `PART_ActivateButton` is real, but it lives **inside the "Selected strategies" list row and is
      not in the UIA tree until that row is expanded.** Searching the dialog for `Activ` returns
      nothing beforehand and one button afterwards.
    - Invoking it then raises `Strategy will remain active`, a modal with a Cyrillic `ОК` (trap 16
      again). Its "Don't show this message again" was ticked on this machine on 2026-08-28, so an
      unattended re-activation is not blocked by it — expect it once on a fresh machine.

24. **ATAS restores a chart strategy STOPPED, and only if the workspace was saved after it was
    added.** Measured across three reboots on 2026-08-28, and the first reading was wrong — it is
    recorded here because the wrong version is the intuitive one:
    - Reboot 1: "Selected strategies" was **empty on both ES charts**. The strategy had been added
      the previous day and the workspace had never been saved since, and the reboot force-closed
      ATAS. It was recorded as "ATAS does not restore strategies". That was wrong.
    - Reboots 2 and 3: the strategy came back, listed in "Selected strategies", **stopped** — the
      row shows the ▶ play control rather than the ■ stop control.

    So the rule is: the workspace persists the strategy if it was saved after the add, and it always
    comes back **stopped**. The bridge only starts on `StrategyStates.Started`, so it never dials in
    and `probe atas` times out — which looks identical to a bridge that failed to load (trap 12) or
    a folder ATAS is not watching (trap 7). Recovery is one click on `PART_ActivateButton`, not a
    re-add — **but check "Selected strategies" before pressing Add**, or you get two bridges
    competing for one named pipe. That happened, and it is only obvious from the icon on each row.

25. **The ATAS SDK marks the synchronous order calls obsolete.** Building against ATAS 8.0.14.397
    emits `CS0618` for `ITradingManager.OpenOrder`, `ModifyOrder`, `CancelOrder` and `ClosePosition`
    — "Use ...Async instead". The adapter calls the synchronous overloads from the bridge's pipe
    thread, which is unmeasured rather than chosen. `IIndicatorDataProvider.DoActionInGuiThread`
    exists, which hints these may be GUI-affine. Settle it before any live order: marshalling
    changes the error path, and rule 3 turns on exactly that.

26. **`tools/win-ps.sh` takes a script on stdin or as a FILE PATH, never as an argument string.**
    Passing the script as `$1` fell through to reading stdin, which under a heredoc-less caller is
    empty — and an empty script runs fine and prints nothing, which reads as "the machine did not
    answer". It now refuses. Three other `win-*.sh` scripts also required env vars they never
    sourced, so they failed on the first call of every session with `TA_WIN_HOST: set TA_WIN_HOST`.


27. **A .NET assembly stores metadata names as UTF-8 and string literals as UTF-16, so the trap-8
    identity check silently fails on literals.** Grepping a freshly built DLL for `AtasStrategyAdapter`
    (a type name) works. Grepping the same DLL for `proven-sameref` (a string literal) returns
    *absent* even though the code is right there — the bytes are UTF-16. That reads as "the build did
    not take", and the natural next move is to rebuild and redeploy something that was already
    correct. Check names as ASCII and literals as `[Text.Encoding]::Unicode`.

28. **A green `dotnet build TradeAgent.sln` did not mean the probe compiled — it was not in the
    solution.** `tools/probe` built only when invoked directly, so a two-argument mistake survived a
    clean local build and a clean test run, and surfaced on the Windows machine mid-run, after a
    push and a bridge rebuild. It is in the solution now. This is trap 8 one level further out: a
    green build was not merely weak proof, it was proof about a different set of files.

29. **A capability that latches on ANY reading stops the search for a better one — and the freeze is
    invisible.** `ProveClientOrderId` returns early once the answer is "final", so while a vacuous
    same-reference match set that latch, the reading this platform actually produces froze the proof
    for the life of the process. A genuinely distinct match arriving later could never be observed,
    and nothing would look wrong, because the diagnostic would go on *truthfully* printing
    `proven-sameref` forever. The latch must follow the strongest reading obtainable, which is a
    different question from whether the capability is true. They are separate predicates now, and
    they are separate deliberately even while they agree.

30. **Reference-distinctness is free across a process restart, so it measures nothing there.** In one
    session, "a different object carried our identifier" is real evidence, because the same-reference
    outcome was available and on real ATAS it is what happened. After a restart the adapter has
    constructed no `Order` at all, so *every* match is reference-distinct by construction. Wire the
    restart proof to `Distinct` and you have not built a proof, you have built an automatic `true` —
    the exact vacuity `SameRef` exists to expose, one level up. It needs its own reading.

31. **The heartbeat runs on its own task, so a wedged command loop reports healthy.** `BridgeServer`
    awaits `HandleFrame` before reading the next frame, so one call into ATAS that never returns means
    no further frame is ever read — including the operator's cancel-all. `StartHeartbeat` is a
    separate `Task.Run` and keeps beating throughout, so `GetHealthAsync` goes on saying `READY`. A
    wedged bridge that reports healthy defeats the one check meant to catch it. Anything that blocks
    on that thread needs its own deadline, and **a deadline that expires is ambiguous, never a
    rejection** — we stopped waiting; ATAS did not, and the order may be live.

32. **`Order.Clone()` copies `Comment`, so an object we built can pass for one ATAS built.** `Modify`
    clones the order to construct its replacement, and the clone carries our client order id while
    `_submitted` still holds the original. Any check that asks only "is this a different object"
    counts it as a round trip the platform performed, when it is one the adapter performed against
    itself. Whether ATAS's collection ever contains that clone is NOT VERIFIED and the guard must not
    depend on the answer.

33. **A .NET assembly's public API is not recoverable off the machine that has it.** There is no ATAS
    NuGet package and no vendor documentation at the depth the bridge needs. The 6,581-line reflection
    dump that 125 checked identifiers rest on lived only in a session scratchpad under `/private/tmp`,
    which macOS clears. It is now `docs/atas-api-8.0.14.397.txt`. Regenerate it with the version in
    the filename after any ATAS upgrade — and note it records **no attributes**, so `[Obsolete]` does
    not appear in it and only a real build reports CS0618.

34. **The bridge is deployed into ATAS by filename prefix, so a NuGet dependency vanishes silently.**
    `AtasInstallation.InstallBridge` copies `Directory.GetFiles(bridgeSourceDir, "TradeAgent.*")` into
    `%APPDATA%\ATAS\Strategies` and nothing else. Every first-party assembly matches, so the filter is
    invisible until the day the bridge's dependency chain acquires a **third-party** one — a NuGet
    package, or the native `e_sqlite3` that `Microsoft.Data.Sqlite` needs. That file is not copied, the
    build is green, the install reports success, and the failure appears inside ATAS as a type load
    with no message anywhere: which is, once again, indistinguishable from traps 12, 7 and 24.

    This is not hypothetical. It is why the bridge-pipe authentication holds its secret in a plain
    file rather than reaching for DPAPI, and why the rule-1 witness design cannot use the SQLite store
    the gateway already has. **Before adding any package reference anywhere in the bridge's chain,
    either widen this filter or confirm the dependency is first-party.**

35. **"No selected strategy" is the settings pane's placeholder, NOT the Selected list being empty —
    and believing it is how you end up with two bridges on one pipe.** The Chart strategies dialog has
    the Selected list on the left and a settings pane on the right; when no row is *highlighted*, the
    right pane reads "No selected strategy". A UIA `find` returns that text with no indication of
    which pane it came from, so it reads exactly like an empty list. Acting on it presses Add over a
    list that already had a bridge in it, and trap 24's two-bridges-one-pipe follows. This happened on
    2026-08-30. **Read the list area itself in a screenshot**; an empty list is blank space under the
    "Selected strategies" header.

36. **The row control is ▶ when stopped and ■ when running, and clicking it toggles — but clicking it
    a second time may only deselect the row.** Confirmed 2026-08-30: ▶ → click → ■ and the chart
    legend goes `[Stopped]` → `[Started]`. The reverse did not work the same way, and the honest
    reading of the state is the **chart legend**, not the icon: it says `[Started]` or `[Stopped]` in
    words. When in doubt, `Delete` the row and re-add — that button behaves predictably.

37. **A window-relative screenshot and a click are in different coordinate spaces.**
    `win-ui.sh shot --window ATAS` returns an image in the window's own pixels; `win-ui.sh click --x
    --y` takes SCREEN pixels. Reading a coordinate off the first and passing it to the second lands
    somewhere else entirely — on 2026-08-30 it opened the Windows *desktop* context menu, which looks
    enough like a misfire inside the app to waste a while. Take `--full` when you need a coordinate,
    and prefer `find` + `invoke --ref` over any coordinate at all: the chart's context menu has
    `Sell Limit at ...` three rows from `Chart strategies`.

39. **`find --window '<title>'` can KILL the UI agent, not merely fail — search globally instead.**
    `find --window 'Authorization' --query Connect` on the ATAS sign-in window timed out at 90 s and
    left the agent dead: `process: NOT RUNNING`, stale heartbeat, and the next call answering "the UI
    agent is not running". The same query **without** `--window` answered instantly and correctly.
    Trap 35's older note recorded the windowed form as answering "no visible window matching", which
    is a far milder failure than losing the agent mid-sequence. Prefer the global `find` always; it
    has never failed. And when the agent dies, `tools/win-agent.sh start` brings it back in seconds.

40. **`PART_ActivateButton` is real, is enabled, and does NOT need the row expanded — the recipe's
    coordinate click is unnecessary.** The 2026-08-30 recipe says "there is no `PART_ActivateButton`
    step", that "searching for `Activ` returns nothing", and to click screen coordinate `1004,641`.
    All three were contradicted on 2026-08-31: with the Chart strategies dialog open,
    `find --query 'Activ'` returned `PART_ActivateButton` directly, and `invoke --ref` on it started
    the strategy (▶ became ■). Clicking a raw coordinate inside a dialog is exactly what trap 37
    warns against; use the named element. **Read the row icon or the chart legend to confirm**, and
    note the strategy still comes back **stopped** after every ATAS restart (trap 24 stands).

41. **The trade CLI can be deployed in a state where it cannot start, and the health row will still
    say READY.** `ToolDeployer` copied `trade.exe` plus three side-cars and none of the seven
    assemblies the CLI loads, so every invocation threw `FileNotFoundException` on `TradeAgent.Core` —
    while the Dashboard said `trade CLI: ready`, because the check was `File.Exists`. Fixed
    2026-08-31 (copy what `trade.deps.json` names; `TradeCliReady` reports what is missing). The trap
    worth keeping is the shape: **the packaged build publishes self-contained single-file, so this
    was invisible in a release and broken in every developer/CI run** — which is the only
    configuration in which anybody exercises the agent path. When a defect can only appear outside
    the packaging you ship, the packaging is not evidence.

42. **Trap 21 came back, and this time TradeAgent itself was the victim.** `win-push.sh` deletes
    `C:\ta\repo\src` before unpacking, and the 2026-08-31 session runs the app from
    `C:\ta\repo\src\TradeAgent.App\bin\Release\net10.0\TradeAgent.exe` — *inside the tree it
    deletes*. Windows refuses to remove a running `.exe` and cheerfully removes everything beside it,
    so the push would have left a half-deleted install that still looks built, exactly as it once did
    to the UI agent. The UI agent was moved to `C:\ta\agent\bin` to escape this; nothing stopped the
    next program from moving in. **`win-push.sh` now refuses before deleting anything** if any running
    process's image path is under `C:\ta\repo`, names it, and exits 1. Verified 2026-08-31 against
    the real machine with TradeAgent running:

    ```
      RUNNING FROM THE REPO: TradeAgent - C:\ta\repo\src\TradeAgent.App\bin\Release\net10.0\TradeAgent.exe
    REFUSING TO PUSH. ...
    win-push exit = 1        # and C:\ta\repo\src was still intact afterwards
    ```

    So: **stop TradeAgent before pushing.** `tools/win-run.sh 'taskkill /IM TradeAgent.exe /F'`.

    **And the corollary, which costs a minute every time it is forgotten:** stopping it means the
    push *succeeds* in deleting `C:\ta\repo\src` — including
    `src\TradeAgent.App\bin\Release\net10.0\TradeAgent.exe`, which is where the app runs from. So
    every push destroys the installed app and it has to be rebuilt before it can be relaunched:
    `dotnet build src\TradeAgent.App\TradeAgent.App.csproj -c Release`. The real fix is to run it
    from outside the repo, the way the UI agent already is.

43. **ATAS cannot be RESTARTED on a disconnected RDP session — it keeps running across a
    disconnect, but it cannot come back up.** This is not trap 19 again. Trap 19 says a disconnected
    session loses only *rendering*; that is true of TradeAgent, of UI Automation and of the bridge,
    and it was true of ATAS too — while ATAS was already running. Relaunch it in that state and it
    signs in, opens its main window, starts building the workspace's chart panels, and dies about
    40 seconds later. Deterministic; reproduced twice on 2026-08-31.

    ```
    Faulting application name: OFT.Platform.exe   Faulting module: coreclr.dll
    Exception code: 0xc0000005
       at OpenTK.Windowing.GraphicsLibraryFramework.GLFWNative.glfwGetVideoMode(Monitor*)
       at OpenTK.Windowing.Desktop.NativeWindow..ctor(NativeWindowSettings)
       at OpenTK.WinForms.GLControl.CreateNativeWindow(GLControlSettings)
    ```

    ATAS renders its charts with an OpenGL control. GLFW cannot enumerate a video mode in a session
    with no rendering surface, and the null it hands back is dereferenced. Once the GL context exists
    it survives a disconnect; creating one fresh in a headless session cannot work.

    **Why this is worth a trap of its own: the crash looks exactly like "the DLL you just deployed
    broke ATAS".** It happens on the first launch after a bridge redeploy, at the moment the
    workspace and its strategies load. Read the stack before believing that — a bridge fault has a
    `TradeAgent` frame in it and this has none. The event log is the fastest route:

    ```bash
    tools/win-ps.sh <<'PS'
    Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='.NET Runtime';
      StartTime=(Get-Date).AddMinutes(-10)} | Select-Object -First 1 |
      ForEach-Object { ($_.Message -split "`r?`n" | Select-Object -First 10) -join "`n" }
    PS
    ```

    **The consequence for planning: any task that needs ATAS restarted needs a rendering surface
    first.** That means reconnecting the RDP session, or rebooting into the console (autologon is
    on). Anything that only *drives* an already-running ATAS is unaffected. Check which kind of task
    you have before you close ATAS, because closing it is the step that cannot be undone remotely.

38. **A market that is closed presents exactly like a bridge that has never seen a price.**
    `quote=none(no-tick)` and `{"at":"0001-01-01T00:00:00+00:00"}` was a real wiring defect on
    2026-08-28 and was simply Sunday on 2026-08-30. Check the day and the chart's last bar before
    debugging the feed. **The workaround is in the workspace already:** the BTCUSDT chart runs on a
    24/7 Binance feed against the simulated `CRYPTO5EB41` account, so order-path work does not have to
    wait for CME to open. Move the bridge to that chart — and remove it from the other one first, or
    see trap 24.

## How the last session was run

2026-08-29 was a full day on the dev Mac with the test machine offline; 2026-08-30 woke it and proved
everything on hardware. Both were run as a manager dispatching agents against written contracts with
hard file-ownership boundaries, integrating each result before dispatching the next wave. Six agents,
no repo collisions. What is worth copying:

- **Repeat: name the thing the agent must not take on trust.** Every finding that mattered came from
  a contract that said what to be suspicious of — "the resume doc says the refusal path moves; check
  whether that is what the code does", "the obvious version of this experiment produces an automatic
  true, find the mechanism that does not". Several came back with defects their brief had not
  predicted, *because* the brief had aimed their suspicion.
- **Repeat: give the agent the established facts so it spends its budget on the unknown.** Briefs that
  quoted prior measurements verbatim and said "do not re-derive these" produced deeper work.
- **Repeat: make agents report defects in code they do not own.** The bridge-pipe hole, the `Modify`
  clone, the unbounded `StopBridge` wait and the heartbeat-carries-capabilities hole all arrived this
  way, from agents sent to do something else.
- **Repeat: demand the tests be proven to bite.** Agents were told to break their own implementation
  and record which test failed for each. That turned "the old tests are blind to this change" from an
  assertion into a measurement — and in one case exposed a blind spot in the *new* tests, where a
  guard was covered by a different assertion than the one that looked like it covered it.
- **Repeat: verify a security claim yourself before acting on it.** The bridge-pipe finding was read
  out of both files by hand before any fix was dispatched.
- **Repeat: on hardware, assert the DEPLOYED artifact and not the built one**, and check the day of
  the week before debugging a feed.
- **Do not repeat: two actors in one file.** `AtasStrategyAdapter.cs` was edited by an agent while
  another read it; the reader's report had to open by reconciling itself with a moved tree.
- **Do not repeat: writing a commit message from intent instead of from the diff.** `1b352d6` claimed
  a reordering it did not contain, and the bug it claimed to fix then surfaced on live ATAS. Read the
  diff before writing the message.
- **Do not repeat: a secret scan that does not gate.** `grep ... ; git commit` runs the commit
  whatever grep found. A machine name reached a commit that way and had to be amended out before the
  push. Wrap it in an `if`.

## Verifying what you inherited

**The bridge on the Windows machine is CURRENT as of 2026-08-30** — rebuilt from this tree, deployed,
and answering with `proto=2` and `auth=ok`. One command confirms it rather than assuming:

```bash
tools/win-run.sh 'cd C:\ta\repo\tools\probe && dotnet run -c Release -- atas --wait 60'
```

`auth=not-presented` or a protocol mismatch means the DLL is older than the tree. Rebuild it — and
remember `AtasStrategyAdapter.cs` is `<Compile Remove>`d off Windows, so **any adapter change you
make on the Mac is unverified by any compiler until this runs**:

```powershell
dotnet build src\TradeAgent.AtasBridge\TradeAgent.AtasBridge.csproj -p:AtasBridgeBuild=true -p:AtasInstallDir="C:\Program Files (x86)\ATAS Platform"
```

Assert the DEPLOYED artifact's identity afterwards rather than trusting the build (trap 8) — and
check type names as ASCII, string literals as UTF-16 (trap 27). ATAS must be closed first; it holds
the DLL.


```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet test TradeAgent.sln        # 235 tests: 67 unit, 130 integration, 38 fault
```

Start any Windows session by asking whether the machine can do the work at all — see
`tools/README.md` for the one-time `~/.tradeagent/win.env` setup. **`win-state.sh` now consults the
UI agent rather than guessing from the session table**, so its verdict distinguishes "cannot drive
the UI" from "can drive it but cannot photograph it" — which are very different days:

```bash
tools/win-state.sh          # reachability, ATAS, the agent, and an honest verdict
tools/win-agent.sh status   # if the agent is the thing that looks wrong
```

The bridge itself is unaffected by any of that. It answers over a named pipe, so `probe atas` works
from the SSH session no matter what the desktop is doing — that is proven, not assumed.

The claims the product stands on, re-runnable on Windows:

```bash
tools/win-push.sh
tools/win-run.sh 'cd C:\ta\repo\tools\probe && dotnet run -c Release -- install codex'
tools/win-run.sh 'cd C:\ta\repo\tools\probe && dotnet run -c Release -- chat codex'
tools/win-run.sh 'cd C:\ta\repo\tools\probe && dotnet run -c Release -- atas'
```

`install` must reach `INSTALL OK` from an empty tools directory. `chat` must print
`NO WINDOW OPENED` **and** `CONVERSATION OK`; it exits non-zero if either fails, so it is safe to
run unattended. `atas` needs the bridge loaded inside ATAS and TradeAgent not running, and it exits non-zero when it
could not reach the bridge rather than inventing a reading. It also carries the rule-1 experiment —
see "Driving ATAS from here" — and refuses to place anything unless the account is provably simulated
from two independent sources. There is deliberately no `--force` and no `--account`.

Build the shipping artifact, with ATAS support, on a machine that has ATAS:

```powershell
packaging\build.ps1 -RequireInstaller -AtasInstallDir "C:\Program Files (x86)\ATAS Platform"
```

The manifest it prints at the end reads the ATAS adapter's presence **out of the compiled assembly**,
not out of the build flag. Check that line rather than trusting the switch.
