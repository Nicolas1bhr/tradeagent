# Resume here

**Read this first, then `BUILD-STATUS.md`.** This file says where the work stopped, what to do next,
and which traps have already been paid for. `BUILD-STATUS.md` says what is proven and what is not,
with the evidence quoted; it is the honest record and it is kept that way deliberately.

Short on purpose. A handoff nobody can afford to read is not a handoff.

---

## The one sentence to carry

**The adapter is rewired onto `ITradingManager` and the reads work — and the machine survived its
first unattended reboot, but came back unable to drive its own desktop and with the bridge gone from
the chart.** Both of those were fixed today; the second one is the more important finding, because
it was silent.

Where that leaves the two booleans the whole product waits on:

- `SupportsClientOrderId` — **still false, still because nothing has been placed.** The bridge
  reports `client_order_id_attempts: 0`, which is the honest reading, not a failure. Step 1 below.
- `SupportsOrderHistory` — **still false, but the false has changed meaning.** It used to mean "could
  not look" (`Connector` was null, so `HistoryCache()` could only return null). It now means
  "looked, and `IIndicatorDataProvider.GetService` threw" — a fact about ATAS rather than about our
  wiring.

## The rule that shapes every design decision

**A terminal is never shown to the user. Not once.** It is the entire reason this product exists:
the underlying capability is already available to anyone willing to use a shell, and what is being
sold is that nobody has to. This rule quietly forbids the obvious implementation of several
features, so before "just shell out to it" feels reasonable, read
`docs/DECISIONS.md` and the class comment on `AtasPrerequisite`.

The rule also *creates* bugs that only exist because of it — see trap 1 below.

## Where the machine is right now (2026-08-28, 15:30)

Verified today, in this order, each with the output quoted in `BUILD-STATUS.md`:

- **The console session, not RDP.** The machine rebooted at 15:04 and logged itself in
  (`session: Active (id 1, console)`, `desktop: live`). Autologon works unattended — that claim is
  now measured rather than configured.
- **Screen capture works again**, because the session is the console one. Every visual judgement
  before today was made against the app on macOS or not at all; `tools/win-ui.sh shot` now returns
  a real desktop at 2560x1440.
- **The UI agent runs from `C:\ta\agent\bin`, NOT from the repo.** This moved today and it is
  load-bearing — see trap 21.
- **ATAS is running, signed in**, portfolio `DEMO15M440CE`, with the rewired bridge added to the ES
  5m chart and **activated**. `probe atas` answers in under a second.

Check all of it in two commands before assuming any of it:

```bash
tools/win-state.sh
tools/win-run.sh 'cd C:\ta\repo\tools\probe && dotnet run -c Release -- atas --wait 60'
```

**The reboot question is answered, and the answer is worse than expected.** ATAS restores the
workspace — both charts, the layout, the account, all four connections — but **does not restore the
chart strategy at all.** After the reboot, "Selected strategies" was empty on *both* ES charts: the
bridge was not merely stopped, it was gone. Recovery is the full re-add below, not a single Start.
Nothing anywhere says the bridge is missing; the pipe simply never answers.

## What to do next, in order

1. **Place one order on the simulated account and read the client order id back.** This is the
   single fact the product waits on, and everything else is now ready for it. Both portfolios are
   simulated — `DEMO15M440CE` (ES@CME) and `CRYPTO5EB41` (BTCUSDT@BinanceFutures), 100,000 each —
   so nothing is at risk.

   `probe atas --place-test-order --yes` places ONE buy limit far below market so it rests unfilled,
   reads it back out of ATAS's own order collection, and cancels it. It refuses outright unless the
   live handshake says `is_simulated: true`.

   The probe reports *why* a false is false, from the bridge's own counters rather than an
   inference: `NOTHING WAS EVER ATTEMPTED`, `ATTEMPTED BUT NEVER CHECKED`, or `THE READ-BACK
   GENUINELY FAILED`. **Only the last is evidence about ATAS.** If that verdict disagrees with the
   order-book reading printed under `AND, INDEPENDENTLY`, believe neither until it is explained.

2. **Settle whether an order-history cache is reachable at all.** `cache=` in `trading_surface` now
   names the exception when a route throws. If every route is dead, `SupportsOrderHistory` is false
   for a known reason and the gateway correctly withholds autonomous live trading — which is an
   answer, and a shippable one. Do not hard-code it true to get past it.

3. **Decide the sync-vs-async order call.** Building the bridge against the real SDK emits four
   `CS0618` warnings: `ITradingManager.OpenOrder`, `ModifyOrder`, `CancelOrder` and `ClosePosition`
   are **obsolete — "Use OpenOrderAsync instead"**. The adapter currently calls the synchronous
   overloads from the bridge's pipe thread. That is unmeasured, not chosen: if step 1 places an
   order cleanly, the sync path works and this is a tidy-up; if it hangs or throws a threading
   error, `IIndicatorDataProvider.DoActionInGuiThread(Action)` exists and marshalling is the fix.
   **Marshalling changes the error path, so settle this before any live order.**

4. **Walk the setup journey and look at it.** Now genuinely doable: captures work
   (`tools/win-ui.sh shot`). Nothing before today had ever been seen on Windows.

5. Only then the staged live trial: paper → extended paper run → one tiny live order →
   disconnect/recovery test → autonomous live permission.

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

- **Does a placed order's client id survive into `TradingManager.Orders`?** Unchanged as the single
  fact that decides `SupportsClientOrderId`, and therefore whether the product may ever trade
  unattended. Nothing has been placed yet, and the bridge says exactly that: `attempts: 0`. Note the
  proof is strict — it only counts for an id TradeAgent itself submitted, read back off the
  platform's own collection, carrying a broker-assigned id too.
- **Are `ITradingManager.Orders` and `ChartStrategy.Orders` the same list?** `trading_surface`
  prints `orders=` and `strategyorders=` side by side; both were 0 with an empty book, so this needs
  one live order to answer. The adapter reads both and de-duplicates by reference identity, so it is
  correct either way — but if they ARE the same list, that de-duplication is load-bearing rather
  than defensive.
- **Is any order-history cache reachable?** `GetService` threw; which exception is now reported.
- **Do the synchronous order calls work off the GUI thread?** See step 3 above. Unmeasured.
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
    Check the DLL for the string `AtasStrategyAdapter`, not the file's existence — step 0 above.

13. **`ChartStrategy.Connector` is null, and nothing says so until runtime.** It exists, it has the
    right type, the code compiles, and every read through it throws "this ATAS chart has no trading
    connection attached yet" on a chart that is demonstrably attached to a portfolio. `Portfolio` on
    the same object is populated. The trading surface for a chart strategy is `ITradingManager`.
    This cost the first live run and it is the reason step 1 above exists.

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

24. **ATAS restores its workspace but NOT its chart strategies.** After a reboot the charts, layout,
    account and all four connections came back exactly as saved, and "Selected strategies" was empty
    on both ES charts. The bridge is not stopped, it is absent. There is no message and no visible
    difference — the pipe simply never answers, which reads identically to a bridge that failed to
    load (trap 12) or a folder ATAS is not watching (trap 7).

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

## How the last session was run

Three agents in parallel against written contracts with hard file-ownership boundaries, then a single
integration pass by the session that dispatched them. What is worth copying:

- **Repeat: give each agent an ownership list and hold to it.** Three agents worked the same tree for
  twenty minutes with zero collisions in the repo. The only collision anywhere was two of them
  writing the same filename in the shared scratchpad — worth giving each agent its own subdirectory.
- **Repeat: send an agent to *disprove* something specific rather than to "look for bugs".** Every
  finding that mattered came from a contract naming the thing it must not take on trust: "read the
  adapter and tell me whether these two states are distinguishable", "prove the file path by running
  the program, not by reading its source". Two of the three came back with defects their brief had
  not predicted, because the brief had told them what to be suspicious of.
- **Repeat: make an agent report defects in code it does not own instead of fixing them.** All three
  did, and the three highest-value changes of the session came out of those reports — integrated by
  one person who could see all three at once.
- **Repeat: verify on the real machine early.** Re-verifying the inherited claims on Windows before
  touching anything took four minutes and meant every later failure had a known-good baseline behind
  it.
- **Do not repeat: editing files while an agent still owns them.** Inherited advice, and it held: the
  bridge protocol fix waited until the probe agent had landed, because that agent was reading those
  files even though it did not own them.

## Verifying what you inherited

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet test TradeAgent.sln        # 107 tests: 43 unit, 28 integration, 36 fault
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
