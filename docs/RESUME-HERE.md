# Resume here

**Read this first, then `BUILD-STATUS.md`.** This file says where the work stopped, what to do next,
and which traps have already been paid for. `BUILD-STATUS.md` says what is proven and what is not,
with the evidence quoted; it is the honest record and it is kept that way deliberately.

Short on purpose. A handoff nobody can afford to read is not a handoff.

---

## The one sentence to carry

**The bridge now runs inside ATAS and answers — and the first thing it proved is that the adapter is
wired to the wrong ATAS surface.** `ChartStrategy.Connector` is null for a chart strategy, so every
read and every order fails with "this ATAS chart has no trading connection attached yet", while
`Portfolio` on the same object is populated. The surface that was wanted is
`ITradingManager` (via the indicator's `IIndicatorDataProvider`), plus `ChartStrategy.OpenOrder`
for placement. **That rewiring is the next task**, and `BUILD-STATUS.md` has the measured evidence
and the member list.

Note `SupportsOrderHistory = false` is **not yet a real answer**: `HistoryCache()` reads
`Connector?.Factory`, and `Connector` is null, so it means "could not look".

**The machine now runs itself.** Autologon is configured (LSA secret, not plaintext), the UI agent
starts at logon, and the whole ATAS journey above was driven from the Mac. **GUI work is no longer a
reason to wait for anybody.**

**That step is GUI work, and there is now a tool that does GUI work.** `tools/winagent` is a resident
UI-Automation agent inside the Windows desktop session, driven by `tools/win-ui.sh` — see
`tools/README.md`. It removes the person from every step except one: **Windows logon**, which needs
the account password and therefore needs you, exactly once. Enable autologon (command in
`tools/README.md`) and the machine logs itself in at every boot, the agent starts itself with it, and
nothing after that waits for anybody.

Until that is done, `tools/win-agent.sh status` says `logged on: NOBODY` and every capture is blank.

## The rule that shapes every design decision

**A terminal is never shown to the user. Not once.** It is the entire reason this product exists:
the underlying capability is already available to anyone willing to use a shell, and what is being
sold is that nobody has to. This rule quietly forbids the obvious implementation of several
features, so before "just shell out to it" feels reasonable, read
`docs/DECISIONS.md` and the class comment on `AtasPrerequisite`.

The rule also *creates* bugs that only exist because of it — see trap 1 below.

## Where the machine is right now (2026-08-28, 00:30)

Left running, and it should still be like this:

- **Autologon is on** for `Nicolas` (LSA secret, not plaintext). The machine logs itself in at boot.
- **The UI agent auto-starts at logon.** `tools/win-agent.sh status` should say `interactive=True`.
- **ATAS is running, signed in**, on portfolio `DEMO15M440CE`, with **TradeAgent Bridge added to the
  ES 5m chart and activated**. It dials the pipe and answers `probe atas` today.
- Nobody needs to be at the machine, and **no monitor needs switching on** — an `LC27G5xT` is
  attached to the Radeon RX 6650 XT.
- **The RDP session is disconnected, and almost everything still works.** Measured, not assumed:
  UI Automation reads the ATAS tree, `invoke` acts on elements, and the bridge answers `probe atas`
  in under a second. **Only screen capture fails** — a disconnected session renders nothing, and
  `shot` returns "the screen could not be captured (The handle is invalid)". So a session can do all
  the work and simply cannot take pictures of it. `tools/win-state.sh` now says exactly this.

Check all of it in two commands before assuming any of it:

```bash
tools/win-agent.sh status
tools/win-run.sh 'cd C:\ta\repo\tools\probe && dotnet run -c Release -- atas --wait 60'
```

If ATAS is not running, bring the whole thing back with the recipe under "Driving ATAS from here".

## What to do next, in order

1. **Rewire the adapter off `Connector` and onto `ITradingManager`. This is the whole job.**
   `ChartStrategy.Connector` is null for a chart strategy — measured, see `BUILD-STATUS.md` — and
   `RequireConnector()` gates all ~12 reads and every order, so the bridge handshakes and then can
   read nothing. What ATAS actually offers a chart strategy:

   ```
   ATAS.Indicators.ITradingManager        (reached from the indicator's IIndicatorDataProvider)
       IEnumerable Orders                 IEnumerable MyTrades
       Portfolio Portfolio                Position Position          Security Security
       event NewOrder / OrderChanged / NewMyTrade / PositionChanged
       event OrderRegisterFailed / OrderCancelFailed / OrderModifyFailed
   ```

   Placement is already on `ChartStrategy` itself: `OpenOrder(Order)`, `OpenOrderAsync(Order)`,
   `CancelOrderAsync(Order)`. The API dump that answers questions like this lives on the machine at
   `C:\ta\atas-api.txt` (267 KB, 694 types) — **read it before guessing a member name.**

   Three things not to lose in the rewrite:
   - `HookConnector()` subscribes to connector events; the equivalents are the `ITradingManager`
     events above. `IsLive()` compares against `Connector` and needs the same treatment.
   - `HistoryCache()` is `Connector?.Factory as IAtasCache`. With `Connector` null it can only
     return null, so **today's `SupportsOrderHistory = false` means "could not look", not "not
     available"** — do not record it as an answer until this is fixed.
   - Rule 1 still stands: `ProveClientOrderId` must read back an id **TradeAgent itself submitted**,
     off the platform's own order collection. `TradingManager.Orders` is that collection now.

2. **Re-run `probe atas`.** `ACCOUNTS VISIBLE` and `ORDERS IN LIVE BOOK` must stop saying
   `COULD NOT READ`. That is the pass/fail for step 1.

3. **Place one order on the simulated account, then probe again.** Both portfolios are simulated —
   `DEMO15M440CE` (ES@CME) and `CRYPTO5EB41` (BTCUSDT@BinanceFutures), 100,000 each — so nothing is
   at risk. This is the reading the whole product waits on: whether ATAS carries a client order id
   back onto a live order, and therefore whether this may ever trade unattended.

   The probe now reports *why* a false is false, from the bridge's own counters rather than an
   inference: `NOTHING WAS EVER ATTEMPTED`, `ATTEMPTED BUT NEVER CHECKED`, or `THE READ-BACK
   GENUINELY FAILED`. **Only the last is evidence about ATAS.** If that verdict disagrees with the
   order-book reading printed under `AND, INDEPENDENTLY`, believe neither until it is explained.

4. **Verify the machine survives a reboot unattended — NOT YET TESTED.** Autologon and the agent's
   at-logon trigger have never actually been through a boot. Reboot, wait, then
   `tools/win-agent.sh status` — `interactive=True` and a capture that is not `uniform: black` mean
   it works headless for good. Also check whether ATAS reopens with the strategy still activated;
   ATAS persists the workspace, but that it survives *activated* is unverified.

5. **Walk the setup journey and look at it.** Now doable from here: `tools/win-ui.sh shot`. Every
   visual judgement before 2026-08-27 was made against the app on macOS.

6. Only then the staged live trial: paper → extended paper run → one tiny live order →
   disconnect/recovery test → autonomous live permission.

## Driving ATAS from here

The whole journey, as actually performed on 2026-08-27. Nobody was at the machine.

```bash
tools/win-agent.sh status                      # interactive=True, or nothing below works
tools/win-ui.sh launch --path 'C:\Program Files (x86)\ATAS Platform\OFT.Platform.exe'
tools/win-ui.sh find --query Authorization     # credentials are saved; just press Connect
tools/win-ui.sh invoke --ref <ConnectButton>
tools/win-ui.sh click --x 760 --y 597 --button right    # on the ES chart, well clear of the DOM
tools/win-ui.sh find --query 'Chart strategies'         # then invoke it
tools/win-ui.sh find --query 'TradeAgent Bridge'        # select it, then Add becomes enabled
tools/win-ui.sh find --ref <dialog> --query Activ       # PART_ActivateButton is Start
```

**Read the UI, do not click at it.** `find` and `tree` return named elements; `invoke --ref` acts on
the one you looked at. The chart's context menu has `Sell Limit at ...` and `Buy Stop at ...` three
rows above `Chart strategies`, and the trading panel has four buttons called `Add`-ish things —
coordinates would eventually hit one of them.

## Open questions nobody has answered

- Does the placed order's `Comment` survive into the platform's own order collection? Still the
  single fact that decides `SupportsClientOrderId`, and therefore whether the product may ever trade
  unattended. **Unchanged by 2026-08-27's run** — nothing has been placed, and the bridge reports
  `client_order_id_attempts: 0` to say exactly that. **Note the proof is stricter than it was:** it
  only counts for an id TradeAgent itself submitted; it used to count for any order in the book
  carrying any comment, which was rule 1 being faked (`BUILD-STATUS.md`, defect 2 of 2026-08-27).
  **And note the collection has moved** — `TradingManager.Orders`, not `Connector.Orders`.
- Is `Connector.Factory` really the `ICache`? That decides `SupportsOrderHistory`, and it is now
  **unanswerable as written**: `Connector` is null for a chart strategy, so `HistoryCache()` can only
  return null and the false it produces means "could not look". Re-ask it after the `ITradingManager`
  rewiring. When no cache is reachable, `GetOrders` **throws rather than returning a short list** if
  asked for a window older than the cache period — a partial history makes "this order does not
  exist" look provable when it is not.
- What is the sign convention on `Position.Volume`? Deliberately unused: getting it wrong would not
  flatten a position, it would double it, so `ClosePosition` lets ATAS pick the side instead.
- Whether Windows Defender Firewall prompts when `codex login` binds its callback socket on port
  1455. A prompt there lands in front of a user the product promised would click Yes exactly once.

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
