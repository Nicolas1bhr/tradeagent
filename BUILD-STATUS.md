# BUILD-STATUS

**Milestone:** The product's two defining promises — *no terminal, ever* and *it installs what it
needs itself* — are now **verified by running them on a real Windows 11 machine**, not inferred. The
ATAS adapter, blocked since the project began because nobody had ATAS, is written and **compiles
against the real ATAS assemblies**, and a releasable installer carrying it has been built.

**Built and verified on:** macOS 26 / arm64 locally, `windows-latest` / `ubuntu-latest` /
`macos-latest` in CI (.NET 10), and a real Windows 11 Pro 26200 machine.
**Target platform:** Windows 11 x64.
**Written with:** Claude Opus 5 (Anthropic), via Claude Code.

The rule this file is written under: every line below is either **verified by running something,
with the output quoted**, or explicitly marked **not verified**. There is no third category.

---

## Verified on real Windows 11 hardware, 2026-08-26

### The AI installs itself, with no terminal and no administrator

From a completely empty tools directory, against the live vendor release:

```
runtime         = OpenAI Codex CLI  install=Download  repo=openai/codex
before install  : installed=False path=<none>
  [00:00] Downloading codex-package-x86_64-pc-windows-msvc.tar.gz — 1,3 MB of 128,3 MB
  ...
  [00:12] Unpacking codex-package-x86_64-pc-windows-msvc.tar.gz
  [00:13] Checking OpenAI Codex CLI runs
  [00:14] OpenAI Codex CLI 0.149.1 is ready
INSTALL OK      : path=...\tools\codex\bin\codex.exe version=0.149.1 managed=True in 00:14
auth state      : Authenticated
```

No administrator rights, no Node.js, no PATH edit, no console window. The download lands under
`%LOCALAPPDATA%\TradeAgent\tools`, which the user already owns.

### The conversation replaces the console, and opens no window

```
  TURN [System] OpenAI Codex CLI is ready.
  TURN [You] Reply with exactly this and nothing else: TRADEAGENT_OK
  TURN [Ai] TRADEAGENT_OK
elapsed         : 00:04   deltas=1   turns=3
visible windows : before=0 after=0  ->  NO WINDOW OPENED
CONVERSATION OK
```

The window count is measured across the whole process tree before and after the turn, not asserted.

### ATAS installs itself, silently

ATAS documents no unattended switches. Its setup is Inno Setup 6.4.3, so Inno's own switches were
tried and the result checked rather than assumed:

```
starting silent install...
exit code: 0
== install dirs ==
C:\Program Files (x86)\ATAS Platform
...
2026-08-26 18:23:04.814   Installation process succeeded.
2026-08-26 18:23:04.814   Need to restart Windows? No
```

592 files, 459 MB, no window shown. The version-selection page that was feared to hide behind
`/VERYSILENT` took its default silently. The installer is Authenticode-signed (`CN=LLC "ATAS"`,
Riga, LV) and its SHA-256 matched a second, independent download.

**The user still needs a free ATAS account** — the platform will not start without one, and
TradeAgent cannot create it. The setup screen says so before the download starts, not after.

### The ATAS adapter compiles against real ATAS

`AtasStrategyAdapter.cs` — 1,046 lines, every `IAtasAdapter` member implemented, no
`NotImplementedException` left — built against the real `ATAS.Strategies.dll`,
`ATAS.Indicators.dll`, `ATAS.DataFeedsCore.dll` and `Utils.Common.dll`:

```
  TradeAgent.AtasBridge -> ...\bin\Release\net10.0-windows\TradeAgent.AtasBridge.dll
Build succeeded.
    1 Warning(s)
    0 Error(s)
```

It was written against a reflection dump of those assemblies (694 types), not from memory, and every
one of the 125 ATAS identifiers it uses was checked against that dump before the compile. The
compile is what confirms the three lifecycle hooks that the dump could not answer (it carries public
members only), because `Indicator.OnCalculate` is abstract and would not have bound.

The order calls were moved off `IDataFeedConnector.RegisterOrder`/`ModifyOrder`/`CancelOrder` — the
compiler flagged all three `[Obsolete]` — onto the current `...Async` forms. An obsolete API on the
path that places real orders is precisely the thing that keeps working until a vendor update.

### The installer installs, per-user, with no prompt

```
installer: 117461626 bytes
exit: 0
== where did it land ==
  FOUND C:\Users\Nicolas\AppData\Local\Programs\TradeAgent  (291 files, 409.8 MB)
== uninstall entry ==
  TradeAgent 0.1.0  [HKCU]
== elevation used? ==
  User privileges: Administrative
  Administrative install mode: No
```

The session running Setup *had* administrator rights and Setup still chose the per-user install —
so an ordinary user sees no consent prompt at all.

### A releasable build, with ATAS support in it

```
== what this build actually contains ==
   staged files      289 files, 405.3 MB
   bridge/           36 files, 32.9 MB
   ATAS adapter      PRESENT - AtasStrategyAdapter is compiled into the bridge assembly
      bridge/TradeAgent.AtasBridge.dll       67.5 KB
   installer         artifacts\TradeAgent-Setup-x64.exe  (112.0 MB)
```

"PRESENT" is read out of the compiled assembly, not out of the build flag. The same bridge without
the adapter is 36.5 KB.

### Tests

`dotnet test TradeAgent.sln` after every change above — **91 passed, 0 failed** (34 unit,
21 integration, 36 fault).

---

## Defects found and fixed today

1. **The AI conversation hung forever, and looked like thinking.** `codex exec` reads stdin *in
   addition to* the prompt argument — it announces `Reading additional input from stdin...` and
   waits for end-of-file. A child that does not redirect stdin inherits the parent's, and TradeAgent
   is a window with no console, so that handle never ends. Measured: the turn stuck at `Busy=true`
   indefinitely; the identical command with stdin closed answered in four seconds. Fixed at the
   class, not the instance — both `AgentSession` and `CliAgentRuntime.Run` now redirect stdin and
   close it immediately. **This bug exists only because there is no terminal**, so no amount of
   testing the CLI by hand would ever have found it.
2. **The bridge could be installed into a folder ATAS never reads.** `StrategyDirCandidates` listed
   the Strategies folder, then the *Indicators* folder, then a `Documents` path from a superseded
   blog post — and detection takes the first that exists. On a machine where the user had added a
   custom indicator but never a strategy, the bridge went to Indicators, ATAS never listed it, the
   heartbeat never arrived, and nothing said why. Indicators is now a separate field and never a
   fallback.
3. **The first ATAS install-directory candidate could never match.** `%ProgramFiles%\ATAS Platform`
   — classic ATAS installs to `Program Files (x86)`. Confirmed by installing it.
4. **`ATAS.exe` and `ATAS.Platform.exe` do not exist.** The real executables are `OFT.Platform.exe`
   and `OFT.PlatformX.exe`.
5. **A fresh ATAS install has no `%APPDATA%\ATAS` at all**, so a perfectly good install reported
   "could not find the ATAS strategies folder". The folder is now created, but only once ATAS itself
   has been found.
6. **The ATAS bridge could not be built at all.** `TargetFramework` was assigned from
   `$(AtasBridgeTargetFramework)` one line *before* that property was defined, so
   `-p:AtasBridgeBuild=true` evaluated it to the empty string.
7. **Every text field in the app rendered in Fluent's default grey** (`#4C4D50`, measured off a
   screenshot), lighter than the card behind it, reading as disabled. Fluent paints the template's
   own border from a nested style, so `TextBox.Background` never applied. The same defect class hid
   in disabled buttons, which reverted to Fluent grey instead of dimming their own colour.
8. **OpenCode was offered as an equal first choice and could not be signed into without a terminal.**
   Its `auth login` reads the provider key from an interactive TTY prompt: no key flag, no device
   code, no URL. The honest instruction would have been "sign in outside TradeAgent", which is a
   terminal by another name. It now has an in-app key field, and Codex is marked recommended and
   listed first.
9. **The build script could reach "Done" having produced nothing.** It now asserts every expected
   artifact exists at a plausible size, and prints what the build actually contains.

---

## What is finished

- **The whole UI.** A dark, code-built design system (`Theme.cs`) with one accent chosen because
  green/amber/red are spent on P&L meaning; an application shell with a persistent risk header and a
  kill switch reachable from every page; the AI conversation as a first-class page; a setup journey
  of framed screens with selectable cards, a step rail and designed empty/loading/error states.
- **Self-installation.** Codex and OpenCode install from their vendors' current GitHub releases,
  resolved at install time; Node.js is available as a portable per-user fallback and is not needed by
  either; ATAS installs from the vendor's own installer behind one Windows consent prompt.
- **No terminal anywhere.** No console is opened by any path. Sign-in runs headless and hands the URL
  to the app to open. `grep -rn "CreateNoWindow" src/` shows no `= false`.
- **The ATAS adapter**, compiling against the real API.

## What does not work yet

- **ATAS has never been run, and nothing has traded through it.** The adapter compiles; not one line
  of it has executed. There is no broker connection on the test machine, and no ATAS account.
- **Two capabilities decide themselves at runtime and are unproven.** `SupportsClientOrderId` flips
  true only after the adapter reads its own client id back off a live order; `SupportsOrderHistory`
  only if `Connector.Factory` really is the `ICache`. **While either is false the gateway refuses
  fully automatic live trading** — which is correct, and must be checked on a live connection before
  anyone relies on it.
- **The Windows GUI was not looked at today.** The desktop was locked, so screen captures came back
  blank. Every visual judgement in this session was made against the app running on macOS. The
  Windows setup journey has not been clicked through since the changes.
- **OpenCode's key sign-in has not been executed.** The file path and JSON shape come from reading
  OpenCode's own source; nothing has written that file and started OpenCode with it.
- **Codex's browser sign-in URL capture has not been exercised on Windows** — the test machine was
  already signed in, so the code path that scrapes the URL out of stderr never ran.
- **The bridge has never been installed into ATAS**, and the five steps the user performs inside ATAS
  have never been walked.
- **The installer is unsigned.** Every user will see "Windows protected your PC" and must click
  More info → Run anyway. On a program that places trades, that wants a certificate.
- **Live money has never been touched.** Correct for this stage.

## Current blockers

1. **An ATAS account and a broker connection.** Everything left is downstream of actually running
   ATAS: the two runtime capability verdicts, the bridge install, the five in-ATAS steps, and any
   claim that an order reached a broker.
2. **A code-signing certificate**, before this goes to anyone who did not build it.

## Next integration target

1. Sign in to ATAS on the test machine, start it, and install the bridge from the app.
2. Read `Describe()` on a live connection and record what `SupportsClientOrderId` and
   `SupportsOrderHistory` actually say.
3. Walk the whole setup journey on Windows with the desktop unlocked, and look at it.
4. Then, and only then, the staged live trial: paper → extended paper run → one tiny live order →
   disconnect/recovery test → autonomous live permission.

## Decisions changed from the brief

| Brief said | Built instead | Why |
|---|---|---|
| WinUI / WPF / Avalonia, pick after comparison | **Avalonia**, code-built UI, no XAML | Only option that builds and runs on the dev/CI host as well as Windows; ships self-contained. |
| Follow the system light/dark theme | **Dark only** | This window sits beside ATAS charts. A light panel between dark charts is the thing that looks broken, and a second palette serves a preference nobody in this audience has expressed. |
| The AI opens in its own window | **A chat page inside the app** | That window was a console, and the console was the product's chat interface. It is the single thing this session existed to delete. |
| Install AI runtimes from code | **Install commands are overridable data** | These vendors change their CLIs on their own schedule. A wrong command is a one-line fix in `runtimes.json`, not a rebuild. |
| Notional cap as a core risk limit | **Opt-in, default off** | One ES future is a six-figure notional on a four-figure margin; any naive cap refuses every legitimate futures order. Contract count is the limit that means something. |
| — | **Added: risk limits, borrowed from venture-agent's `policy.yaml`** | The brief had modes and a kill switch but no bound on size. |
| — | **Added: a fresh price is required for every order** | An agent sizing a market order from a stale quote was reachable. |
| — | **Added: autonomous live trading refused on unprovable backends** | If a connector cannot round-trip a client order id and serve order history, post-disconnect state is unknowable. |
| Operator control over IPC | **Operator authority is in-process only** | Mode, kill switch, live activation and approvals are not reachable from the agent-facing pipe, so an agent wanting more permission has nowhere to ask. |

## Honest note on scope

Two things this cannot change, and the brief does not claim otherwise:

- Retail latency and information access do not compete with firms running colocated systems and paid
  news feeds. This product's value is a safe, auditable, controllable execution chain — not an edge.
- Every safety property is proven against a simulator and a loopback bridge. They are *designed* to
  hold against ATAS and a real broker; they are not yet *proven* to. That is what the staged live
  trial is for.
