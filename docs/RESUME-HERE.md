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

## What to do next, in order

0. **DONE on 2026-08-27, but check it again after any reinstall.** The installed TradeAgent must
   contain a bridge that has ATAS compiled into it. The copy on the test machine did not — it was a
   protocol-only stub, and pressing "Install the add-on" would have copied that stub into ATAS where
   no amount of refreshing could ever have listed it (trap 12). It has been rebuilt with
   `-AtasInstallDir`, installed, and verified in place: `AtasStrategyAdapter: True`. Fifteen seconds
   to re-check, and worth it every time the app is reinstalled:

   ```bash
   tools/win-ps.sh <<'EOF'
   $d = "$env:LOCALAPPDATA\Programs\TradeAgent\bridge\TradeAgent.AtasBridge.dll"
   $t = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($d))
   "AtasStrategyAdapter: " + $t.Contains("AtasStrategyAdapter")
   EOF
   ```

   False means rebuild and reinstall before going anywhere near ATAS:
   `packaging\build.ps1 -RequireInstaller -AtasInstallDir "C:\Program Files (x86)\ATAS Platform"`,
   then run the installer with `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`. It is a per-user install
   and it leaves `%LOCALAPPDATA%\TradeAgent` — the trading records and onboarding progress — alone.

1. **Install the bridge and get it listed inside ATAS.** In TradeAgent, press "Install the add-on".
   Then in ATAS: open a chart → Strategies for that chart → **press refresh if TradeAgent Bridge is
   not listed** → Add → Start. ATAS does not watch the Strategies folder; the app now says so on the
   step itself, but it is still the thing that wastes the first twenty minutes if forgotten.

   All of this is drivable from here once somebody is logged on — start by reading the UI rather
   than clicking at it:
   ```bash
   tools/win-agent.sh status
   tools/win-ui.sh launch --path 'C:\Program Files (x86)\ATAS Platform\OFT.Platform.exe'
   tools/win-ui.sh wait --window ATAS --timeoutMs 120000
   tools/win-ui.sh tree --window ATAS --depth 6
   ```
2. **Run the instrument and record the answer.**
   ```bash
   tools/win-run.sh 'cd C:\ta\repo\tools\probe && dotnet run -c Release -- atas --wait 180'
   ```
   TradeAgent must NOT be running — it owns the bridge pipe, and the bridge would dial into it
   instead of the probe. Exit 0 means the bridge answered and the output is the record; a capability
   reading of `false` is a valid answer and still exits 0.
3. **Expect `SupportsClientOrderId = false` on a fresh session, and do not treat it as a fault.**
   Rule 1 makes it false until an order has proved it. Place one paper order, then run the probe
   again. **That second reading is now capable of changing** — before 2026-08-27 it was not, because
   the bridge only ever sent its capabilities once, at the handshake. See trap 9.

   The probe now prints *why* it is false, from counters the bridge reports rather than from an
   inference: `false BECAUSE NOTHING WAS EVER ATTEMPTED`, `ATTEMPTED BUT NEVER CHECKED`, or
   `AND THE READ-BACK GENUINELY FAILED`. Only the last is evidence about ATAS. Read that line before
   concluding anything, and if the reported verdict disagrees with the order-book reading printed
   under `AND, INDEPENDENTLY`, believe neither until the disagreement is explained.
4. **Walk the whole setup journey on Windows and look at it — from the console, not over RDP.**
   Screen captures cannot photograph an RDP desktop: `win-shot.sh` lands on the physical console,
   which is a different desktop, and captures blank. Every visual judgement so far was made on macOS.
5. Only then the staged live trial: paper → extended paper run → one tiny live order →
   disconnect/recovery test → autonomous live permission.

## Open questions nobody has answered

- Does the placed order's `Comment` survive into `Connector.Orders`? Still the single fact that
  decides `SupportsClientOrderId`, and therefore whether the product may ever trade unattended.
  **Note the proof is now stricter than it was:** it only counts for an id TradeAgent itself
  submitted. It used to count for any order in the book carrying any comment, which was rule 1 being
  faked — see `BUILD-STATUS.md`, defect 2 of 2026-08-27.
- Is `Connector.Factory` really the `ICache`? That decides `SupportsOrderHistory`. When no cache is
  reachable, `GetOrders` **throws rather than returning a short list** if asked for a window older
  than the cache period — a partial history makes "this order does not exist" look provable when it
  is not.
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
`tools/README.md` for the one-time `~/.tradeagent/win.env` setup:

```bash
tools/win-state.sh
```

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
