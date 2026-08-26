# Resume here

**Read this first, then `BUILD-STATUS.md`.** This file says where the work stopped, what to do next,
and which traps have already been paid for. `BUILD-STATUS.md` says what is proven and what is not,
with the evidence quoted; it is the honest record and it is kept that way deliberately.

Short on purpose. A handoff nobody can afford to read is not a handoff.

---

## The one sentence to carry

**The product installs itself and never shows a terminal — both proven by running them on real
Windows — and the ATAS adapter now compiles against real ATAS, but not one line of it has ever
executed.** Everything remaining is downstream of actually running ATAS with an account and a broker.

## The rule that shapes every design decision

**A terminal is never shown to the user. Not once.** It is the entire reason this product exists:
the underlying capability is already available to anyone willing to use a shell, and what is being
sold is that nobody has to. This rule quietly forbids the obvious implementation of several
features, so before "just shell out to it" feels reasonable, read
`docs/DECISIONS.md` and the class comment on `AtasPrerequisite`.

The rule also *creates* bugs that only exist because of it — see trap 1 below.

## What to do next, in order

1. **Sign in to ATAS on the Windows machine and start it.** ATAS is installed
   (`C:\Program Files (x86)\ATAS Platform`) but has never been run, and it will not start without a
   free ATAS account. That account is the user's to create; the app says so before the download.
2. **Install the bridge from the app** (setup step "Installing the ATAS bridge"), then perform the
   five in-ATAS steps the app lists. Note ATAS does *not* watch the Strategies folder — the user must
   click the blinking button in ATAS's strategy list before the bridge appears at all.
3. **Read `Describe()` on a live connection** and record what `SupportsClientOrderId` and
   `SupportsOrderHistory` actually report. Both decide themselves at runtime and both are currently
   unproven. **While either is false the gateway refuses fully automatic live trading** — that is
   correct behaviour, not a bug, and nobody should "fix" it by hard-coding true.
4. **Walk the whole setup journey on Windows with the desktop unlocked**, and look at it. Every
   visual judgement in the last session was made against the app running on macOS.
5. Only then the staged live trial: paper → extended paper run → one tiny live order →
   disconnect/recovery test → autonomous live permission.

## Open questions nobody has answered

- Does the placed order's `Comment` survive into `Connector.Orders`? That single fact decides
  `SupportsClientOrderId`, and therefore whether the product may ever trade unattended.
- Is `Connector.Factory` really the `ICache`? That decides `SupportsOrderHistory`. When no cache is
  reachable, `GetOrders` **throws rather than returning a short list** if asked for a window older
  than the cache period — a partial history makes "this order does not exist" look provable when it
  is not.
- What is the sign convention on `Position.Volume`? Deliberately unused: getting it wrong would not
  flatten a position, it would double it, so `ClosePosition` lets ATAS pick the side instead.
- Has anyone signed OpenCode in through the in-app key field? The file path and JSON shape were read
  out of OpenCode's own source; nothing has written that file and started OpenCode with it.

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

## How the last session was run

Six agents in parallel against a written contract with hard file-ownership boundaries, then a single
integration pass. Two things worth repeating and one worth not:

- **Repeat: give research its own leg, and demand a source URL per fact.** It corrected two errors in
  its own brief — `npm --prefix` without `-g` produces no launcher at all, and `sst/opencode` now
  301-redirects to `anomalyco/opencode`. Both would have failed silently on a customer's machine.
- **Repeat: verify on the real machine early.** Installing ATAS took twenty minutes and unblocked
  work that had been stuck since the project began.
- **Do not repeat: editing files while an agent still owns them.** Doing so mid-flight produced a
  half-renamed tree and cost a recovery pass. Freeze the diff, then integrate.

## Verifying what you inherited

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet test TradeAgent.sln        # 91 tests: 34 unit, 21 integration, 36 fault
```

The two claims the product stands on, re-runnable on Windows — see `tools/README.md` for setup:

```bash
tools/win-push.sh
tools/win-run.sh 'cd C:\ta\repo\tools\probe && dotnet run -c Release -- install codex'
tools/win-run.sh 'cd C:\ta\repo\tools\probe && dotnet run -c Release -- chat codex'
```

`install` must reach `INSTALL OK` from an empty tools directory. `chat` must print
`NO WINDOW OPENED` **and** `CONVERSATION OK`; it exits non-zero if either fails, so it is safe to
run unattended.

Build the shipping artifact, with ATAS support, on a machine that has ATAS:

```powershell
packaging\build.ps1 -RequireInstaller -AtasInstallDir "C:\Program Files (x86)\ATAS Platform"
```

The manifest it prints at the end reads the ATAS adapter's presence **out of the compiled assembly**,
not out of the build flag. Check that line rather than trusting the switch.
