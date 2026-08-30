# Resume here

**Read this first, then `BUILD-STATUS.md`.** This file says where the work stopped, what to do next,
and which traps have already been paid for. `BUILD-STATUS.md` says what is proven and what is not,
with the evidence quoted; it is the honest record and it is kept that way deliberately.

Short on purpose. A handoff nobody can afford to read is not a handoff.

---

## The one sentence to carry

**Rule 1 is proven.** An order was placed, ATAS was shut down, and the identifier was found again on
an order in the restarted platform's own collection — alongside the broker id the dead run had
recorded before it ended.

```
BRIDGE SESSION : 1ce7ec65        RECORD SESSION : bccb57cf
ORDER SURVIVED : YES — broker id 12007918
IDENTIFIER     : YES — client_order_id = TA-PROBE-20260830120255
RULE 1         : PROVEN ACROSS A PROCESS RESTART. THIS IS THE ANSWER.
```

The process that read it had constructed no `Order` at all, so the match cannot be our own object —
which is exactly what made every earlier reading worthless. **In-session the reading is still
`proven-sameref` and still reports false**, on two different connectors, so that is how ATAS's
collection works rather than one backend's quirk.

**The bound is real and is printed in the verdict itself:** a cross-session match cannot separate ATAS
rebuilding the order from *the broker's* answer on reconnect, from ATAS rehydrating it out of its own
local store. The identifier survives ATAS restarting, which is what reconciliation needs. Whether it
ever reached the broker is a different question that only the broker's own report answers.

Where the two autonomy gates stand:

- `SupportsClientOrderId` — **true, on evidence**, since 2026-08-30.
- `SupportsOrderHistory` — **false, for a known reason.** `GetService<T>()` throws
  `NotSupportedException` for *every* type including one reachable as a property on the same
  interface, so no cache route exists. That is an answer, and a shippable one.

`ReconciliationProvable` is false and the gateway refuses `LIVE_AUTONOMOUS`. **That is still correct:**
one gate is now open on evidence, the other is shut on an answer. Do not "fix" the second by
hard-coding it.

## The rule that shapes every design decision

**A terminal is never shown to the user. Not once.** It is the entire reason this product exists:
the underlying capability is already available to anyone willing to use a shell, and what is being
sold is that nobody has to. This rule quietly forbids the obvious implementation of several
features, so before "just shell out to it" feels reasonable, read
`docs/DECISIONS.md` and the class comment on `AtasPrerequisite`.

The rule also *creates* bugs that only exist because of it — see trap 1 below.

## Where the machine is right now (2026-08-29)

**OFFLINE, and it was offline for the whole of the 2026-08-29 session.**

```
$TA_WIN_NAME   windows   active; relay "ams"; offline, last seen 9h ago
```

Nothing can be proven until somebody wakes it. `home-server` is on the same tailnet but refuses this
machine's SSH key, so there is no wake-on-LAN route from the dev Mac — it needs a person.

What was true when it was last up (2026-08-28, 17:15), and every line of it needs re-checking rather
than assuming:

- **Console session, autologon, and the UI agent all came back by themselves**, verified through three
  unattended reboots. The agent runs from `C:\ta\agent\bin` — **not** the repo, and that is
  load-bearing (trap 21).
- **ATAS was running, signed in, portfolio `DEMO15M440CE`,** with the bridge added to the ES 5m chart
  and activated.
- **Nothing was resting and there was no position:** `orders=0 strategyorders=0 mytrades=0 position=0`.
- The quote-timestamp fix was deployed and confirmed: `age=1383s` where it had been `8544s`.

**The bridge deployed there will be REFUSED by this build, and that is expected.**
`BridgeProtocolVersion` went 1 → 2 on 2026-08-29 and the pipe now authenticates, so the DLL sitting in
`%APPDATA%\ATAS\Strategies` speaks a protocol this connector rejects and holds no secret it can prove.
The probe names both cases rather than timing out. Nine commits of adapter and protocol changes postdate
it and **none has been through a compiler** — rebuild first, redeploy, then read.

Swapping the bridge DLL means closing ATAS (traps 22, 23, 24). The reliable route is a reboot: it
force-closes ATAS, releases the DLL, and re-verifies the unattended-recovery path in one go, at about
70 seconds — but a force-close does **not** save the workspace, so save it first or the strategy does
not come back at all.

Check it in two commands before assuming any of it:

```bash
tools/win-state.sh
tools/win-run.sh 'cd C:\ta\repo\tools\probe && dotnet run -c Release -- atas --wait 60'
```

## What to do next, in order

**Nothing below can be started until somebody wakes the machine.** Everything that could be done
without it was done on 2026-08-29.

1. **Rebuild and redeploy the bridge before believing any reading from that machine.** Nine commits of
   adapter and protocol changes have never been through a compiler — `AtasStrategyAdapter.cs` is
   `<Compile Remove>`d off Windows — and `BridgeProtocolVersion` went 1 → 2, so **the bridge deployed
   there will be refused by this build**. That is expected and the probe now says so by name rather than
   timing out.

   ```
   dotnet build src/TradeAgent.AtasBridge/TradeAgent.AtasBridge.csproj -p:AtasBridgeBuild=true -p:AtasInstallDir="C:\Program Files (x86)\ATAS Platform"
   ```

   Assert the artifact's identity rather than trusting the build (trap 8), checking type names as ASCII
   and string literals as UTF-16 (trap 27). Swapping the DLL means closing ATAS; the reliable route is a
   reboot, but save the workspace first or the strategy does not come back at all (traps 22, 23, 24).

2. **Settle rule 1 from a source that cannot be our own object** — and read the trap first, because the
   obvious implementation produces an automatic `true` rather than a proof.

   The cheapest real source is a **fresh ATAS session**: place a resting order, restart ATAS, read the
   book. Anything surviving a process restart cannot be our instance. The obstacle is that after a
   restart `_submitted` is empty and `ProveClientOrderId` refuses any id not in it — which is the
   deliberate 2026-08-27 safety fix and must not be weakened.

   **THE TRAP: after a restart this process has constructed no `Order` at all, so every match is
   reference-distinct by construction — and `Distinct` is what sets the capability.** Relax the guard and
   you do not get a proof, you get a reading true by construction dressed as a measurement: exactly the
   vacuity `SameRef` was invented to expose, one level up.

   So it needs a reading of its own — `CrossSession` — with the latch following it rather than
   `Distinct`, and a durable write-ahead record of which ids this product submitted: written *before* the
   order exists, by a process gone by the time it is read, carrying the broker id that process saw ATAS
   assign. Full design, including probe command shapes and the exact text that is proof versus disproof,
   in the 2026-08-29 section of `BUILD-STATUS.md`. **It cannot use the gateway's SQLite store — see
   trap 34.**

   **And bound what it would prove.** A cross-session match cannot separate ATAS rebuilding the order
   from the *broker's* answer on reconnect from ATAS rehydrating it out of its own local store. Both
   survive a restart and look identical from inside a chart strategy. Only the broker's own report
   separates them, and that is not a source the software can read at runtime during an outage.

3. **Measure whether `OpenOrderAsync` completes on submission or on broker acknowledgement.** The single
   gate on flipping the four obsolete order calls. If acknowledgement, blocking on it puts `Place` past
   `AtasConnector`'s 10s RPC timeout and turns **every** order into UNKNOWN.

   The reason to want the switch is not what this file used to say. It is not rule 3's classification,
   which the switch does not touch. It is that **the call deadline covers one of five write paths** — the
   other four are synchronous and cannot be given a deadline from this side, so a block in any of them
   still stops the pipe loop while the heartbeat reports READY.

4. **Look at the Windows GUI, finally.** Captures work; nothing in the app itself has ever been seen on
   Windows. Two specific things now want eyes: the setup journey end to end, and the new bridge-refusal
   sentence on the dashboard status row — it is around 450 characters, longer than any `StatusDetail`
   before it, and no truncation was found in the UI but nobody has looked at it on a screen.

5. Only then the staged live trial: paper → extended paper run → one tiny live order →
   disconnect/recovery test → autonomous live permission.

**Done on 2026-08-29, so do not redo it:** the bridge pipe now authenticates in both directions and an
unproved peer can no longer unlock autonomy through a hello, a heartbeat or an event; `SupportsClientOrderId`
no longer reports true on a same-reference match; an object the adapter built can no longer pass for one
ATAS built; ATAS calls and bridge teardown have deadlines; and the probe explains a refusal instead of
timing out.

## Driving ATAS from here

The whole journey, as actually performed on 2026-08-28. Nobody was at the machine, and every step
below is a command that ran.

```bash
tools/win-agent.sh status                      # interactive=True, or nothing below works
tools/win-ui.sh launch --path 'C:\Program Files (x86)\ATAS Platform\OFT.Platform.exe'
tools/win-ui.sh find --window 'Authorization' --query Connect   # credentials are saved
tools/win-ui.sh invoke --ref <ConnectButton>                    # ~20s to the main window
tools/win-ui.sh click --x 770 --y 607 --button right            # on the LEFT ES chart, clear of the DOM
tools/win-ui.sh find --window 'ATAS -' --query 'Chart strategies'   # then invoke it
tools/win-ui.sh find --query 'TradeAgent Bridge'                # a TreeItem in AVAILABLE strategies
tools/win-ui.sh select --ref <that TreeItem>                    # this is what ENABLES Add
tools/win-ui.sh invoke --ref <Add>
```

**Then the part that is not obvious and cost most of the time.** After Add, the strategy sits in
"Selected strategies" **stopped**, and starting it is neither the `IsActivated` checkbox nor a
button you can find:

```bash
tools/win-ui.sh click --x 1005 --y 643                  # the ▶ expander on the Selected row
tools/win-ui.sh find --ref <dialog> --query Activ       # NOW PART_ActivateButton exists
tools/win-ui.sh invoke --ref <PART_ActivateButton>
# then dismiss "Strategy will remain active" — see trap 23 — and press ОК on Chart strategies
```

**Closing ATAS**, which nothing could do before today:

```bash
tools/win-ui.sh windows                                  # find the main hwnd (isMain=true)
tools/win-ui.sh close --hwnd <that>                      # a real WM_CLOSE from inside the session
tools/win-ui.sh find --query 'Save current workspace'    # the modal it raises
tools/win-ui.sh tree --ref <that> --depth 5              # "Save and close" / "Close without saving"
tools/win-ui.sh click --ref <Save and close>
```

**Read the UI, do not click at it.** `find` and `tree` return named elements; `invoke --ref` acts on
the one you looked at. The chart's context menu has `Sell Limit at ...` and `Buy Stop at ...` three
rows above `Chart strategies`, and the trading panel has four buttons called `Add`-ish things —
coordinates would eventually hit one of them. The coordinates above are the two exceptions, and both
are into empty chart space rather than at a control.

## Open questions nobody has answered

- **Does ATAS carry our client order id onto anything we did not write?** Still the single fact that
  decides `SupportsClientOrderId` and therefore whether the product may ever trade unattended — but it
  is no longer untouched. One order has been placed and the read-back matched **our own object**
  (`coid=proven-sameref`), which proves only that ATAS assigned an `Order.Id`. Step 2 above is the
  route to an answer and names the trap in it.
- **Does `OpenOrderAsync`'s task complete on SUBMISSION or on broker ACKNOWLEDGEMENT?** The gate on
  flipping the four obsolete order calls, and unanswerable off Windows. If acknowledgement, blocking on
  it puts `Place` past the connector's 10s RPC timeout and turns every order into UNKNOWN.
- **Are `ITradingManager.Orders` and `ChartStrategy.Orders` the same list?** `trading_surface` prints
  `orders=` and `strategyorders=` side by side; both were 0 with an empty book, so this still needs a
  live order to answer. The adapter reads both and de-duplicates by reference identity, so it is
  correct either way — but if they ARE the same list, that de-duplication is load-bearing rather than
  defensive.
- **Does ATAS's order collection ever contain `Modify`'s cloned replacement?** Unanswerable from the
  API dump, which carries public members only. It decides whether trap 32 is live or merely possible;
  the guard against it does not depend on the answer, and must not be "simplified" until it is known.
- ~~**Is any order-history cache reachable?**~~ **Answered: no.** `GetService<T>()` throws
  `NotSupportedException` for every type, including one reachable as a property on the same interface.
  The control probe is what makes that an answer rather than "try another type".
- **Do the order calls work off the GUI thread?** The synchronous ones do — one live data point, an
  order placed from the bridge's pipe thread on 2026-08-28. The Async ones have never been called.
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
    This cost the first live run, and it is why the rule-1 question in step 2 above is asked of
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

38. **A market that is closed presents exactly like a bridge that has never seen a price.**
    `quote=none(no-tick)` and `{"at":"0001-01-01T00:00:00+00:00"}` was a real wiring defect on
    2026-08-28 and was simply Sunday on 2026-08-30. Check the day and the chart's last bar before
    debugging the feed. **The workaround is in the workspace already:** the BTCUSDT chart runs on a
    24/7 Binance feed against the simulated `CRYPTO5EB41` account, so order-path work does not have to
    wait for CME to open. Move the bridge to that chart — and remove it from the other one first, or
    see trap 24.

## How the last session was run

2026-08-29 was run entirely from the dev Mac with the test machine offline, as a manager dispatching
agents against written contracts with hard file-ownership boundaries, integrating each result before
dispatching the next wave. Five agents, no repo collisions. What is worth copying:

- **Repeat: name the thing the agent must not take on trust.** Every finding that mattered came from a
  contract that said what to be suspicious of — "the resume doc says the refusal path moves; check
  whether that is what the code does", "the obvious version of this experiment produces an automatic
  true, find the mechanism that does not". Three of five came back with defects their brief had not
  predicted, *because* the brief had aimed their suspicion.
- **Repeat: give the agent the established facts so it spends its budget on the unknown.** Briefs that
  quoted the prior measurements verbatim and said "do not re-derive these" produced deeper work than
  briefs that left the agent to rediscover the ground.
- **Repeat: make agents report defects in code they do not own.** The bridge-pipe hole, the `Modify`
  clone and the unbounded `StopBridge` wait all arrived this way, from agents sent to do something
  else entirely.
- **Repeat: demand the tests be proven to bite.** One agent was told to break its own implementation
  seven ways and record which test failed for each. That is what turned "the two old rejection tests
  are blind to this change" from an assertion into a measurement — they appeared in none of the seven
  failure lists.
- **Repeat: verify a security claim yourself before acting on it.** The bridge-pipe finding was read
  out of both files by hand before any fix was dispatched. It held; the habit is what makes it worth
  believing when it does.
- **Do not repeat: two actors in one file.** `AtasStrategyAdapter.cs` was edited by an agent while
  another read it for a different question, and the reader's report had to open by reconciling itself
  with a tree that had moved under it. It cost nothing this time. It will.
- **Watch for the doc drifting ahead of the code.** This file told the last session to redeploy a fix
  that was already deployed, and told this one that switching to the async overloads moves the refusal
  path, which it does not. A handoff that is confidently wrong costs more than one that is silent.

## Verifying what you inherited

**The bridge on the Windows machine is several commits behind and must be rebuilt before any reading
from it means anything.** `AtasStrategyAdapter.cs` is `<Compile Remove>`d off Windows, so the rule-1
capability change, the call deadline and everything after them have never been through a compiler:

```powershell
dotnet build src\TradeAgent.AtasBridge\TradeAgent.AtasBridge.csproj -p:AtasBridgeBuild=true -p:AtasInstallDir="C:\Program Files (x86)\ATAS Platform"
```

Assert the artifact's identity afterwards rather than trusting the build (trap 8) — and check type
names as ASCII, string literals as UTF-16 (trap 27).


```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet test TradeAgent.sln        # 169 tests: 43 unit, 90 integration, 36 fault
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
run unattended. `atas` is the step-3 instrument: it needs the bridge loaded inside ATAS, TradeAgent
not running, and it exits non-zero when it could not reach the bridge rather than inventing a
reading.

Build the shipping artifact, with ATAS support, on a machine that has ATAS:

```powershell
packaging\build.ps1 -RequireInstaller -AtasInstallDir "C:\Program Files (x86)\ATAS Platform"
```

The manifest it prints at the end reads the ATAS adapter's presence **out of the compiled assembly**,
not out of the build flag. Check that line rather than trusting the switch.
