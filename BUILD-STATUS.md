# BUILD-STATUS

**Milestone:** the bridge runs inside ATAS and its reads work. The adapter was wired to a surface
ATAS never fills for a chart strategy (`ChartStrategy.Connector` is null); rewired onto
`ITradingManager`, accounts and orders now read back off the live platform where an hour earlier
both said `COULD NOT READ`. Separately, the machine took its first unattended reboot: it logged
itself in, and came back **unable to drive its own desktop** because a repo push had silently
half-deleted the UI agent hours earlier. Both are fixed and both are quoted below.

The two capability verdicts are still false, and the two falses are no longer the same kind of
thing: `SupportsOrderHistory` now means "looked, and the call threw", while `SupportsClientOrderId`
still means "nothing has ever been placed". **The one fact the product waits on — whether ATAS
carries a client order id onto a live order — remains unmeasured.** The product's two defining
promises — *no terminal, ever* and *it installs what it needs itself* — remain verified by running
them on real Windows 11.

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

## Verified on real Windows 11 hardware, 2026-08-27

Re-verified the inherited claims on the machine before changing anything, then the new work.

### The inherited baseline still holds

```
dotnet --version            10.0.400
ATAS assemblies present     ATAS.Strategies.dll ATAS.Indicators.dll ATAS.DataFeedsCore.dll ATAS.Types.dll
dotnet build TradeAgent.sln 0 Warning(s)  0 Error(s)
bridge vs REAL ATAS         1 Warning(s)  0 Error(s)
dotnet test                 34 + 36 + 21 = 91 passed, 0 failed
```

### ATAS is signed in, and the platform answered two open questions

The user created the ATAS account and signed in. `%APPDATA%\ATAS` went from **absent** to fully
populated — `Connectors.cnf`, `Instruments.cnf`, `TraderSettings.cnf`, `Workspaces_v3`, `Chart`,
`Database` — written 01:22–01:27 on 2026-08-27. `probe atas` then read the platform itself:

```
ATAS INSTALLED        : YES
ATAS INSTALL DIR      : C:\Program Files (x86)\ATAS Platform
ATAS VERSION          : 8.0.14.397
ATAS RUNTIME TFM      : net10.0
                        read from the platform's own runtimeconfig. A bridge built for a different
                        framework is not rejected with an error — ATAS simply never lists it.
ATAS RUNNING          : YES
LAYOUT VERIFIED       : YES
STRATEGY FOLDER       : C:\Users\Nicolas\AppData\Roaming\ATAS\Strategies
```

That settles **A1 question 2** in `docs/RESEARCH-REQUIRED.md`, which had stood unverified with a
default of `net8.0-windows`. The bridge builds `net10.0-windows`, which matches. Had the old guess
shipped, ATAS would have silently never listed the strategy, with nothing anywhere saying why.

**Still not measured:** `BRIDGE IN STRATEGIES : NO — no TradeAgent.AtasBridge.dll`. The bridge has
never been loaded, so `SupportsClientOrderId` and `SupportsOrderHistory` remain unknown. `probe atas`
exits 1 and names what was missing rather than guessing.

### The rule-1 safety fix compiles against real ATAS

Verified the way trap 8 requires — the change asserted present **on the machine** before believing
any build:

```
== trap 8: did the change actually reach the machine? ==
  FOUND guard at line 1016
== build bridge against REAL ATAS ==
    1 Warning(s)
    0 Error(s)
  TradeAgent.AtasBridge.dll  67.5 KB  built 01:41:03
```

### Tests, on Windows, after every change above

```
Passed!  - Failed: 0, Passed: 36, Total: 36 - TradeAgent.FaultTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 43, Total: 43 - TradeAgent.UnitTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 23, Total: 23 - TradeAgent.IntegrationTests.dll (net10.0)
```

**102 passed, 0 failed** (was 91).

---

## Verified on macOS against the real vendor binaries, 2026-08-27

Two first-run paths that were `NOT VERIFIED` are now proven — on macOS, against the vendors' own
release binaries, using **no credential of any kind** (the only key used anywhere was the literal
string `sk-FAKE-tradeagent-probe-0000`; no browser was opened and no sign-in completed).

### Codex's browser sign-in URL capture — PROVEN on macOS

Codex 0.150.0, `CODEX_HOME` pointed at an empty directory — the genuinely-not-signed-in state the
Windows machine could not reach because it was already signed in. Raw vendor stderr:

```
Starting local login server on http://localhost:1455.
If your browser did not open, navigate to this URL to authenticate:

https://auth.openai.com/oauth/authorize?response_type=code&client_id=app_EMoamEEZ73f0CkXaXp7hrann&...
```

`BeginAuthenticationAsync` returned the second address, in 0.2s. The manifest comment claiming
"callback first, real address second" is correct — measured offsets `[32]` and `[124]`. Two details
worth keeping: the callback is printed as plain **`http`**, so the `https` requirement alone excludes
it; and codex emits **no ANSI escapes** when its output is a pipe, so `Ansi.Strip` is not what makes
this path work — it is load-bearing for OpenCode, which does colour its output.

### OpenCode's key sign-in — PROVEN on macOS

OpenCode 1.18.23, `HOME` redirected to a scratch directory, `XDG_DATA_HOME` deliberately left unset
so that finding the file proves `$HOME/.local/share` specifically. Before:

```
┌  Credentials  ~/.local/share/opencode/auth.json
└  0 credentials
```

OpenCode **names the file itself** — the path is the program's own word, not an inference from its
source. After `SignInWithApiKeyAsync` wrote 63 bytes there:

```
┌  Credentials  ~/.local/share/opencode/auth.json
●  OpenAI  api
└  1 credentials
```

`OpenAI` and `api` are OpenCode reading the provider key **and the `type` field** back out of the
record, which is what makes this a proof of the JSON shape and not only of the path. The manifest's
`AuthStateSuccessPattern` was confirmed in both polarities, and `auth list` exits 0 either way — so
the comment saying the exit code means nothing is right, and the plural in "1 credentials" matters.

Codex's stdin key branch was proven the same way, both polarities of `login status` included.

**A caveat that applies to both, now written into the manifests:** Codex accepted an obviously fake
key without contacting OpenAI. `AuthState.Authenticated` means *a credential is on disk*, never
*the credential works*.

### What the macOS run does NOT prove — stated plainly

- `%USERPROFILE%\.local\share\opencode\auth.json` is untested, in two independent ways: that the
  expansion lands where the profile is, and that `opencode.exe` reads there.
- Every Windows-only branch of executable resolution: PATHEXT, the `.cmd` npm shim, and `SetCommand`
  routing `.cmd`/`.bat` through `ComSpec`. If Windows Codex resolves to a `.cmd` shim, login output
  is pumped through `cmd.exe` — a configuration this run never entered.
- Whether `codex.exe` prints the same text, on stderr, without escapes. Different build, and the
  Windows machine ran 0.149.1 against macOS's 0.150.0.
- That `codex login` binds a listening socket on port 1455, and **whether Windows Defender Firewall
  prompts for it** — a prompt in front of a user the product promised would click Yes only once.
- Nothing here moves `Verified` off `false` on either manifest. That flag means proven on Windows.

**Also corrected by running it:** `codex login` does *not* short-circuit when already signed in — it
returns a fresh authorize URL. So `BeginAuthenticationAsync`'s "finished without printing a URL"
branch is not what an already-signed-in Codex hits, and the gate must remain `AuthStateArgs`.

---

## Verified 2026-08-27, later session: the protocol can now say *why*

Two of the open questions in `docs/RESUME-HERE.md` were design questions with one right answer, and
both are now implemented, compiled against the real ATAS assemblies, and tested. Neither has yet run
**inside** ATAS — see the honest note at the end of this section.

### A false `SupportsClientOrderId` now says which false it is

`BridgeHello` carries `client_order_id_attempts` and `client_order_id_checks`. Both are `int?`, and
null is a distinct answer from zero: a bridge that reports nothing has not told anyone it attempted
nothing. Nothing derives a capability from either — `ConnectorCapabilities` is untouched.

The probe reports them instead of inferring. Run against a stand-in bridge on macOS, all three
states render and are distinguishable — this is real output, three separate runs:

```
SUBMITTED WITH AN ID  : 0   (orders this bridge sent to ATAS carrying a client order id)
READ-BACKS PERFORMED  : 0
CLIENT ID VERDICT     : false BECAUSE NOTHING WAS EVER ATTEMPTED. This says nothing about ATAS.
HOW THIS WAS DERIVED  : REPORTED BY THE BRIDGE. ...

SUBMITTED WITH AN ID  : 2
READ-BACKS PERFORMED  : 0
CLIENT ID VERDICT     : false, ATTEMPTED BUT NEVER CHECKED — the round trip has not failed either.

SUBMITTED WITH AN ID  : 3
READ-BACKS PERFORMED  : 2
CLIENT ID VERDICT     : false, AND THE READ-BACK GENUINELY FAILED. This IS evidence about ATAS.
```

Only the third is evidence about ATAS. Before this, all three were the same byte on the wire and the
probe said so, labelling its own order-book reading **inferred, not reported**. That inference is
still printed, under `AND, INDEPENDENTLY`, precisely because it comes from a different source: in the
third run above the two **disagree** (the stand-in's order book is empty), and the probe says to
believe neither until that is explained. That is the intended behaviour, not a defect in the output.

### A version-mismatched bridge names itself, and still gains nothing

`AtasConnector` kept refusing an incompatible hello — `_hello` stays null, so `Capabilities` reports
nothing supported and the gateway cannot trade on anything it claimed. What changed is that the
**identity** is kept separately, in `AtasConnector.Incompatible`, and reaches the dashboard as the
health detail on the failed row:

```
bridge 9.9.9 speaks protocol 2, this build speaks 1 — reinstall the add-on from TradeAgent
```

Version strings from a refused peer are untrusted text on the way to a label, so they are stripped of
control characters and clipped to 40 characters first. A test asserts both halves at once — the
version survives, and not one of the four capabilities the mismatched bridge asserted got through —
because the dangerous fix is the one that keeps the version by keeping the whole frame.

### It compiles against real ATAS

The adapter half of this change is excluded from every non-Windows build, so macOS cannot check it.
Built on the Windows machine against the real assemblies:

```
  TradeAgent.AtasBridge -> C:\ta\repo\src\TradeAgent.AtasBridge\bin\Release\net10.0-windows\TradeAgent.AtasBridge.dll
Build succeeded.
    1 Warning(s)
    0 Error(s)
```

Same one warning as the inherited baseline. The source was asserted to have arrived first — trap 8 —
by grepping the remote file for `_clientOrderIdChecks` (3 hits) before believing the build.

### Tests

```
Passed!  - Failed: 0, Passed: 36, Total: 36 - TradeAgent.FaultTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 43, Total: 43 - TradeAgent.UnitTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 28, Total: 28 - TradeAgent.IntegrationTests.dll (net10.0)
```

**107 passed, 0 failed** (was 102). The five new ones are integration tests over real named pipes:
the counters travelling on a post-handshake frame, null-is-not-zero for a bridge that reports
neither, the incompatible bridge naming itself while gaining nothing, the clipping of its version
string, and the status row being re-announced when that bridge disconnects. The last one was
checked the only way worth checking: it fails against the code without the fix (`Failed: 1`).

### The test machine could not have loaded the bridge, and now can

The copy of TradeAgent installed on the test machine shipped a bridge assembly with **no ATAS
adapter in it** — the protocol-only stub `packaging/build.ps1` produces when it is not given
`-AtasInstallDir`. Read out of the two DLLs directly:

```
installed  AtasStrategyAdapter : ABSENT      (37,376 bytes, 08/26 18:54)
installed  ChartStrategy ref   : ABSENT
fresh      AtasStrategyAdapter : PRESENT     (69,632 bytes, 08/27 13:33)
fresh      ChartStrategy ref   : PRESENT
```

Pressing "Install the add-on" would have copied that stub into `%APPDATA%\ATAS\Strategies`, where it
loads without complaint and contributes no strategy — so ATAS would have listed nothing, with no
message anywhere saying why. See trap 12: that symptom is indistinguishable from trap 1, whose fix
(press refresh) is the first thing anyone tries and could never have worked.

Rebuilt with ATAS support. The manifest reads the adapter out of the compiled assembly, not out of
the build flag, which is the line worth checking:

```
== what this build actually contains ==
   version           0.1.0
   staged files      289 files, 405.4 MB
   bridge/           36 files, 32.9 MB
   ATAS adapter      PRESENT - AtasStrategyAdapter is compiled into the bridge assembly
      bridge/TradeAgent.AtasBridge.dll       68.0 KB
   installer         artifacts\TradeAgent-Setup-x64.exe  (112.0 MB)
```

Installed it, silently and per-user, and read the result back out of the installed file rather than
trusting the exit code:

```
installer: 117486505 bytes
exit code: 0
--- installed bridge afterwards ---
  69632 bytes  08/27/2026 13:40:32
  AtasStrategyAdapter  : True
  ATAS.Strategies ref  : True
  ClientOrderIdAttempts: True
```

The machine is now in a state where step 1 can actually succeed. `PrivilegesRequired=lowest` and the
installer's `[UninstallDelete]` leaves `%LOCALAPPDATA%\TradeAgent` alone, so the trading records and
onboarding progress survived the reinstall.

### Tests on Windows, after all of the above

```
Passed!  - Failed: 0, Passed: 36, Total: 36 - TradeAgent.FaultTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 43, Total: 43 - TradeAgent.UnitTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 28, Total: 28 - TradeAgent.IntegrationTests.dll (net10.0)
```

### `tools/win-ps.sh` long-script path, previously NOT VERIFIED, now verified

An 8,440-byte script exceeds the encoded-command limit and travels as a file. It ran:

```
LONG SCRIPT PATH REACHED: C:\ta\win-ps-tmp.ps1
host: <redacted: host names stay out of the repo>
```

### THE BRIDGE RAN INSIDE ATAS, 2026-08-27

The blocker that had stood since the project began is gone. The bridge was installed, added to a
chart and started **entirely from the dev Mac**, with no person touching the Windows machine, and it
dialled in. This is the first time a single line of `AtasStrategyAdapter` has ever executed.

```
BRIDGE PIPE           : ANSWERED after 00:00
{"v":1,"op":"hello","data":{"bridge_protocol_version":1,"bridge_version":"8.0.14",
 "atas_version":"8.0.14.397","account_id":"DEMO15M440CE","is_simulated":true,
 "supports_client_order_id":false,"client_order_id_attempts":0,"client_order_id_checks":0,
 "supports_order_history":false,"supports_modify":true,"supports_close_position":true}}
PROTOCOL VERDICT      : MATCH — Versions.BridgeCompatible(1) = True
CONNECTOR HANDSHAKE   : OK — AtasConnector accepted the same bridge
```

The two counters added earlier the same day travelled off the **real** adapter, so the probe reported
rather than inferred — the NOT VERIFIED note above them is now closed:

```
SUBMITTED WITH AN ID  : 0
READ-BACKS PERFORMED  : 0
CLIENT ID VERDICT     : false BECAUSE NOTHING WAS EVER ATTEMPTED. This says nothing about ATAS.
```

Both accounts on the machine are simulated — `DEMO15M440CE` (ES@CME) and `CRYPTO5EB41`
(BTCUSDT@BinanceFutures), each 100,000 balance. No real money is reachable from this configuration.

### What survives an RDP disconnect — measured

The owner disconnected, leaving the session `Disc (id 2)` and the desktop `LOCKED`. Everything was
re-tested in that state rather than reasoned about:

```
agent           : running (pid 4884, session 2), heartbeat 0s ago, interactive=True
UI Automation   : WORKS   — 13 elements read off the live ATAS window
the bridge      : WORKS   — BRIDGE PIPE : ANSWERED after 00:00, full hello, handshake OK
screen capture  : FAILS   — Win32Exception: The handle is invalid.
```

So a disconnected session can do all of the work and simply cannot photograph it. That distinction
matters because the agent previously reported a single `can_drive_ui: true` covering both, which was
a lie in precisely the case it existed to catch. It now reports `can_automate` and `can_capture`
separately and settles the second by attempting a one-pixel grab. `tools/win-state.sh` reads the same
two facts off the heartbeat, so the first command of any session says what is actually available.

### And it immediately found a real defect: the adapter is wired to the wrong ATAS surface

Every read and every order in `AtasStrategyAdapter` goes through `RequireConnector()`, which returns
`ChartStrategy.Connector` (an `IDataFeedConnector`). **ATAS leaves that null for a chart strategy.**
Measured, not guessed — the same run that handshook successfully:

```
ACCOUNTS VISIBLE      : COULD NOT READ — ConnectorTransportException: this ATAS chart has no
                        trading connection attached yet
ORDERS IN LIVE BOOK   : COULD NOT READ — ConnectorTransportException: ...
```

It is not a timing problem (a second run minutes later reads the same) and it is not a chart
misconfiguration: `Portfolio` **is** populated on the very same object — the hello carried
`account_id: DEMO15M440CE`, which `Describe()` reads from `Portfolio.AccountID`. So the strategy is
attached to a portfolio while `Connector` is null.

The reflection dump names the surface that was wanted. `ATAS.Indicators.ITradingManager`, reached
from the indicator's `IIndicatorDataProvider`, carries exactly what the adapter reads:

```
interface ATAS.Indicators.ITradingManager
    IEnumerable`1 MyTrades { get; }      IEnumerable`1 Orders { get; }
    Portfolio Portfolio { get; }         Position Position { get; }
    Security Security { get; }
    event Action`1 NewOrder              event Action`1 OrderChanged
    event Action`1 NewMyTrade            event Action`1 PositionChanged
    event Action`2 OrderRegisterFailed   event Action`2 OrderCancelFailed
```

and order placement is already on `ChartStrategy` itself: `Void OpenOrder(Order)`,
`Task OpenOrderAsync(Order)`, `Task CancelOrderAsync(Order)`.

**This is the next piece of work, and it is well specified:** move the ~12 `RequireConnector()` call
sites and `HookConnector()`'s event wiring onto `TradingManager`, keep `Connector` only where a data
feed is genuinely meant, and re-run `probe atas`. `SupportsOrderHistory` is reported false today, but
that reading is **not yet trustworthy** — `HistoryCache()` is `Connector?.Factory as IAtasCache`, and
`Connector` is null, so false there means "could not look", not "not available".

**Why the compile did not catch this.** `Connector` exists, is the right type, and returns null at
runtime — there was nothing for the compiler to reject. It is the class of defect only a live run
finds, which is the entire argument for building the instrument before trusting the integration.

### A tool that presses the buttons, 2026-08-27

`tools/winagent` is a resident UI-Automation agent for the Windows desktop session, driven by
`tools/win-ui.sh`. It exists because every remaining step is GUI work inside ATAS. Compiled on the
machine, `0 Warning(s) 0 Error(s)`.

What is verified is the part that must be: **that it knows when it cannot work.** Run from the SSH
session, which has no desktop:

```
{"ok":true,"data":{"pid":9480,"session":0,"interactive":false,"desktop":"Default",
 "screen":"1024x768","user":"...","can_drive_ui":false}}
```

`can_drive_ui:false` is the correct answer there, and `win-ui.sh` refuses in 1.2 s when no heartbeat
is fresh rather than hanging for its full 90-second timeout:

```
{"ok":false,"error":"the UI agent is not running (no fresh heartbeat). tools/win-agent.sh status"}
```

The scheduled task is registered with an at-logon trigger and correctly declined to start with nobody
logged on: `not started: nobody is logged on, so there is no interactive session to start it into.`

**NOT VERIFIED, and it is most of the tool:** no screenshot, no UI tree, no click, no keystroke and no
`launch` has ever run on a real desktop, because there has not been one. Every op above the transport
is compile-checked only. Treat the first run against a live session as a bring-up, not as a regression.

**The one thing it cannot do for itself is Windows logon** — that needs the account password, so it
needs the owner, once. Sysinternals Autologon is staged at `C:\ta\tools\autologon\` and its
signature checked (`status: Valid`, `CN=Microsoft Corporation`). `tools/README.md` carries the command
and states what enabling it trades away.

### NOT VERIFIED

- **No counter has ever been produced by the real `AtasStrategyAdapter`.** The increments compile
  against real ATAS and nothing more; the values seen above came from a stand-in bridge. Until the
  bridge is loaded into ATAS this is exactly the same standing as every other adapter claim.
- **The incompatible-bridge line has never been seen on screen.** The wiring is real —
  `TradingGateway.OnConnectionChanged` passes the detail and `Ui.Describe` renders `failed — …` — but
  it is asserted by test, not photographed, and producing it needs two builds of the bridge with
  different protocol versions. The status column trims with an ellipsis, which is why the version
  number is at the front of the string and the advice at the end: what gets cut is the recoverable
  half.

---

## Verified on real Windows 11 hardware, 2026-08-28

### The adapter was reading a surface ATAS never fills, and now it is not

`ChartStrategy.Connector` is null for a chart strategy. `RequireConnector()` gated all twelve reads
and every order, so the bridge handshook and could then read nothing. Rewired onto `ITradingManager`
via the indicator's `IIndicatorDataProvider`. The same probe verb, before and after, same machine,
same chart, same account:

```
before   ACCOUNTS VISIBLE      : COULD NOT READ — ConnectorTransportException: this ATAS chart has
                                 no trading connection attached yet
         ORDERS IN LIVE BOOK   : COULD NOT READ — ConnectorTransportException: this ATAS chart has
                                 no trading connection attached yet

after    ACCOUNTS VISIBLE      : 1 — DEMO15M440CE (USD, simulated=true, trading=true)
         ORDERS IN LIVE BOOK   : 0
```

The hello frame now carries the adapter's own account of what it bound to, read off the live bridge:

```
TRADING SURFACE       : DataProvider=ok TradingManager=ok Connector=null orders=0 strategyorders=0
                        mytrades=0 portfolio=DEMO15M440CE security=ES position=none
                        cache=none(connector-null,getservice-threw)
```

Built against the real ATAS **8.0.14.397** SDK on the Windows machine. The installed DLL's identity
was asserted by reading the compiled bytes rather than trusting the build (trap 8):

```
  AtasStrategyAdapter  : present
  ITradingManager      : present
  TradingSurface       : present
  RequireConnector     : absent
```

**`SupportsOrderHistory` is still false, and its meaning has changed.** It used to mean "could not
look". It now means "looked, and `IIndicatorDataProvider.GetService` threw" — a fact about ATAS
rather than about our wiring. Still not hard-coded true.

**`SupportsClientOrderId` is still false, and its meaning has NOT changed:** `client_order_id_attempts`
is 0. No order has been placed, so the round trip has not been attempted, let alone failed.
**NOT VERIFIED: whether ATAS carries a client order id onto a live order.** That is the one fact the
product waits on and it is untouched by today's work.

**NOT VERIFIED: whether the synchronous order calls work off the GUI thread.** Building against the
real SDK emits four `CS0618` warnings — `ITradingManager.OpenOrder`, `ModifyOrder`, `CancelOrder`
and `ClosePosition` are obsolete, "Use ...Async instead". The adapter calls the synchronous
overloads from the bridge's pipe thread. Nothing has exercised that path.

### An order was placed, and the proof of rule 1 turned out to be worthless

The first order this product has ever placed. Simulated account `DEMO15M440CE`, one buy limit,
quantity 1, priced 10% below the bid and rounded DOWN so it could not fill, cancelled at the end.
ATAS took it and handed it back carrying both identifiers:

```
CLIENT ORDER ID       : TA-PROBE-20260828170111
THE ORDER             : BUY LIMIT 1 ES @ 6977.75  TIF=Day  on DEMO15M440CE
PLACE CALL            : RETURNED — ATAS took the order without a definite refusal.
ORDERS BEFORE         : 0
ORDERS AFTER          : 1
CARRIES OUR ID        : YES — client_order_id = TA-PROBE-20260828170111
CARRIES A BROKER ID   : YES — connector_order_id = 7968887
SUBMITTED WITH AN ID  : 1   (was 0 before the order, +1)
READ-BACKS PERFORMED  : 3   (was 0 before the order, +3)
SupportsClientOrderId : true   AFTER the attempt — this is the reading that counts.
```

**And that `true` is not evidence, which is the whole finding:**

```
ROUND TRIP, MEASURED  : proven-sameref — ATAS handed back THE VERY OBJECT we submitted.
                        THE PROOF IS VACUOUS
RULE 1                : NOT SATISFIED — THE MATCH IS REAL AND IT PROVES NOTHING.
```

`Place` constructs an `Order`, sets `Comment` on it, and hands that instance to
`ITradingManager.OpenOrder`. ATAS's `Orders` collection then contains **that same object**, so
"our identifier came back" is true by construction: it never left. The only thing actually
observed is that ATAS assigned `Order.Id = 7968887`.

Had the adapter not been instrumented to compare by reference, this run would have reported rule 1
satisfied and the product would have been one boolean away from autonomous live trading on a proof
that proves nothing. **`SupportsClientOrderId = true` must not be believed on this platform.**

**NOT VERIFIED, and it is now the question the product waits on:** whether ATAS carries the
identifier onto the *broker's* order. Nothing observable from inside a chart strategy can settle it,
because everything a chart strategy can read may be our own object. It needs a source that cannot
be: the platform's order history, a fresh ATAS session, or the broker's own report.

Two things this run also confirmed, live:

- The resting order read back `"filled_quantity": 0, "state": "WORKING"`. Before the fix landed
  earlier the same day it would have read FILLED, because `Unfilled` defaults to 0 and the code
  computed `quantity - Unfilled`.
- Cleanup worked and nothing was left behind. Verified from a *separate* probe run afterwards:
  `orders=0 strategyorders=0 mytrades=0 position=0`.

### Order history is unreachable, and now for a known reason

The cache walk's control probe settles what three sessions of `false` could not. It asks
`GetService<ITradingManager>()` — a type reachable as a property on the very same interface — and
compares by reference:

```
cache=none(factory=connector-null,
           svc:probe=threw(NotSupportedException:The-service-of-type-ATAS.Indicators.ITradingManager-is-not-regis),
           svc:ICache=threw(NotSupportedException:The-service-of-type-ATAS.DataFeedsCore.Database.ICache-is-not-re),
           svc:IEntityFactory=threw(NotSupportedException:...))
```

The control throws too. `IIndicatorDataProvider.GetService<T>()` registers nothing usable, so every
cache route is dead and `SupportsOrderHistory = false` is an **answer** rather than a gap. Without
the control probe, `svc:ICache=threw` would have read as "try another type".

Consequence, and it is the correct one: `ReconciliationProvable` is false, and `TradingGateway`
refuses `LIVE_AUTONOMOUS` with `AUTONOMY_REQUIRES_PROVABLE_STATE`. Paper and attended live trading
are unaffected.

### The bridge had never seen a price

`_quotes` was fed only from `IDataFeedConnector` events, and `Connector` is null, so no tick had ever
arrived. The order test is what found it, by refusing to place:

```
QUOTE (raw)      : {"symbol":"ES","at":"0001-01-01T00:00:00+00:00"}
REFUSED TO PLACE : THE QUOTE CARRIES NO USABLE BID.
```

Wired to `IOnlineDataProvider.BestBidAskChanged` / `NewTrades`, with `ChartStrategy.BestBid/BestAsk`
as an on-demand fallback. Live afterwards:

```
quote=event(bid=7753.75,ask=7754.00,age=8544s,kind=unspecified)
```

That reading also settled `MarketDataArg.Time`'s `DateTimeKind`, which the API dump does not state.
8544s is ~2 hours over the true age; this machine is UTC+2 and the feed is dxFeed 15-minute delayed,
so **ATAS stamps UTC and labels it `Unspecified`**. Corrected. The guard that unsets `At` for any
quote stamped more than 60s in the future stays, because that is a measurement of one platform on
one machine and the sign of the error flips west of Greenwich.

**Verified after redeploying the correction**, same machine, same feed, ~40 minutes later:

```
quote=event(bid=7764.50,ask=7764.75,age=1383s,kind=unspecified)
```

`8544 - 7200 = 1344`, and this reads 1383 — the two-hour offset is gone and what remains is the
dxFeed delay plus the gap since the last tick. Unspecified is UTC on this platform.

### The machine survives an unattended reboot — and came back unable to drive itself

Autologon had been configured but never taken through a boot. It works:

```
== machine ==
  session          : Active (id 1, console)
  desktop          : live
  uptime           : 0d 00:01
```

Reboot to SSH answering was ~34 seconds, with nobody at the machine and no monitor switched on.
Screen capture works again on the console session — `shot --full` returned a real 2560x1440 desktop
(`uniform: null`), where a disconnected RDP session had returned "the handle is invalid".

**But the UI agent did not come back**, and that is the more important finding:

```
== UI agent ==
  agent            : NOT RUNNING - tools/win-agent.sh status
lastRunTime  : 08/28/2026 15:04:41
lastResult   : 0x80008083
```

`0x80008083` is the .NET host's `CoreHostLibMissingFailure`. Cause, confirmed by inspection: the
agent's output directory held `winagent.exe` and `winagent.dll` but **no `winagent.runtimeconfig.json`**.
`win-push.sh` clears `C:\ta\repo\tools` before unpacking and the agent ran from there; Windows
refuses to delete a running `.exe` but deleted the unlocked JSON beside it, under `-EA 0`, so the
push reported success and the already-loaded agent kept working for hours. Fixed: the agent now runs
from `C:\ta\agent\bin`, the deploy fails loudly if the runtimeconfig is absent, and the push
reports what it could not delete. Re-verified end to end:

```
Build succeeded.
    0 Error(s)
deployed: C:\ta\agent\bin (runtimeconfig present)
process        : running (pid 3736, session 1)
session        : 1   interactive=True
```

**NOT VERIFIED: that the agent now survives a reboot from its new location.** The move was made
after the reboot, so the at-logon path has not been exercised since. One reboot settles it.

### ATAS restores its workspace but not its chart strategies

After the reboot ATAS reopened with both charts, the layout, the account `DEMO15M440CE` and all four
connections green — and **"Selected strategies" empty on both ES charts**. The bridge was not
stopped, it was absent. `probe atas` timed out with `BRIDGE PIPE : NO ANSWER within 60s`, which
reads identically to a bridge that failed to load or a folder ATAS is not watching.

Recovery is the full re-add, and the recipe is in `docs/RESUME-HERE.md` because two steps of it are
not discoverable: the `IsActivated` checkbox in the settings grid cannot be toggled
(`ChartStrategy.IsActivated` is `{ get; }`), and `PART_ActivateButton` does not exist in the UIA
tree until the "Selected strategies" row is expanded.

### A modal dialog was invisible, and a modal was the answer every time

ATAS was asked to close three ways — UIA `Invoke` on `PART_CloseButton`, a physical click on it, and
ALT+F4 — and stayed running each time. None was ignored: each raised a modal the tooling could not
see, because `windows` enumerated one `MainWindowHandle` per process. With capture unavailable at
the time, UI Automation was the only sense available and it was blind exactly where it mattered.

After the fix, the same `close` produced the signature on the first try:

```
hwnd=  656008 owner=  197312 main=False enabled=True  title=Save current workspace?
hwnd=  197312 owner=       0 main=True  enabled=False title=ATAS - [Default workspace]
```

— an enabled owned window in front of a disabled main window. The dialog's own "Save and close"
button then exited ATAS cleanly, which is what finally released the bridge DLL for redeployment.
The same op later showed the three-deep stack raised by activating a strategy
(`Strategy will remain active` → `Chart strategies` → main window), all three states correct.

**Tests:** 107/107 green on macOS after the rewrite (43 unit, 28 integration, 36 fault).

---

## Defects found and fixed on 2026-08-26

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

## Defects found and fixed on 2026-08-27

Seven, and the first two are the ones that mattered.

1. **A capability that only becomes true after the handshake could never reach the gateway — so the
   staged live trial could not finish.** `BridgeServer` sent `adapter.Describe()` exactly once per
   pipe connection, at `Hello`; heartbeats carried `{v, op}` and no data. Rule 1 makes
   `SupportsClientOrderId` false until a placed order has proved it, so the proof arrived strictly
   *after* the only moment anyone read it. `AtasConnector` kept the handshake's answer for the life
   of the connection, the gateway went on refusing `LIVE_AUTONOMOUS`, and the intended path — trade
   in "Real, ask me first", prove the id, then enable "Real, fully automatic" — had no way to reach
   its last step short of restarting ATAS. Reproduced against the real `BridgeServer` and real
   `AtasConnector` over a real named pipe. Fixed: the heartbeat now carries the current `Describe()`.
   Chosen over a change-triggered frame deliberately — a lost change notification leaves the two ends
   permanently disagreeing, which is the same class of bug being fixed, whereas a lost heartbeat is
   repaired by the next one. **This hid because `LoopbackAtasAdapter` reports the capability true
   from the first frame, and a capability that is true immediately never has to travel.**
2. **SAFETY: `SupportsClientOrderId` could be set true by an order TradeAgent never placed.**
   `OnOrderPayload` fed `ProveClientOrderId` the `Comment` of *every* order crossing the feed, and
   `ProveClientOrderId` never consulted `_submitted` — the dictionary of ids TradeAgent actually
   submitted. Any order in ATAS's book carrying any comment, placed by hand or by another strategy,
   set the latch. With an order cache reachable that is the whole of `ReconciliationProvable`, so the
   gateway would have permitted `LIVE_AUTONOMOUS` on a round trip nobody performed. Rule 1 says read
   *its own* identifier back and says **do not fake it**. Fixed inside `ProveClientOrderId`, so the
   guarantee holds regardless of caller.
3. **The credential path could hang forever, with no error and no timeout.**
   `SignInWithApiKeyAsync`'s stdin branch drained stderr *to end* before reading stdout, so a CLI
   that fills the stdout pipe blocks writing, never exits, and never closes stderr. Measured against
   a stand-in that reads the key exactly as codex does — 64 KB returned in 0.2s, 128 KB never
   returned at all. Latent today (codex writes nothing on stdout for `login --with-api-key`) and
   latent only until a vendor makes that command chatty; the symptom would be a sign-in spinner
   turning forever, which is trap 1 reappearing on the one path that handles the user's credential.
   Fixed to `Run()`'s shape — both pipes drained concurrently, 30s deadline, kill on expiry. Verified
   against the same reproduction: 128 KB now returns in 0.3s, 512 KB in 1.1s, 4 MB in 8.4s.
4. **Nothing in the product ever showed the two capabilities that decide autonomy.** The gateway
   refused at the moment an order was dispatched — the worst possible moment to find out. "Check
   everything" now reports them: `DEGRADED` rather than `FAILED` (nothing is broken, nothing is
   repairable, and three of the four modes work), and worded **"not confirmed"** rather than
   "cannot", because a `false` from a fresh ATAS session means *nothing has been placed yet*, not
   *your broker is incapable*. A test fails the build if that copy ever says "cannot", "unable" or
   "does not support".
5. **The setup journey walked the user into a dead end.** Trap 7 — ATAS does not watch its Strategies
   folder, and the add-on is not listed until ATAS is told to look again — was recorded in the
   handoff and **never reached the user**. Step 4 said "Choose TradeAgent Bridge" over what would be
   an empty list, immediately after the app claimed it had installed the add-on. It now carries the
   reason.
6. **The system-check screen dropped the reason and kept only the advice.** For a non-READY check it
   rendered `UserAction` and discarded `Detail`, on a screen that promises anything missing is "named
   below, with what to do about it". The checks that suffered most were the ones whose action is
   necessarily generic: every gateway health row says "See the activity history for what happened",
   so the row read identically whether the trouble was no connection, no account or a stale bridge.
7. **The adapter's own class doc contradicted its code on rule 2.** The summary stated
   `SupportsOrderHistory` "is a hard false"; `Describe()` computes it at runtime from a type test.
   Anyone reading the summary would have believed the value settled and skipped the one measurement
   step 3 exists for.

Plus one in the harness itself: `tools/win-state.sh` reported a live RDP desktop as locked, because
it asked whether *any* `LogonUI` was running. Windows keeps one in the physical console session
whenever that console sits at the lock screen — permanently, on a machine only ever reached over RDP.
It is now session-aware, and says so.

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

- **The bridge has never been installed into ATAS.** `probe atas` on 2026-08-27 reported
  `BRIDGE IN STRATEGIES : NO`, and `%APPDATA%\ATAS\Strategies` still held 0 files at the end of that
  day. The five steps the user performs inside ATAS have never been walked. What *is* now true is
  that the installed app finally carries a bridge ATAS could load, so pressing the button can work.
- **Nothing has traded through ATAS.** ATAS is now signed in and running, but not one line of
  `AtasStrategyAdapter` has ever executed, and there is no broker connection on the test machine.
- **The two capabilities are still unmeasured.** `SupportsClientOrderId` turns true only after the
  adapter reads its own client id back off a live order; `SupportsOrderHistory` only if
  `Connector.Factory` really is the `ICache`. **While either is false the gateway refuses fully
  automatic live trading** — correct, and not to be "fixed" by hard-coding either true. `probe atas`
  is now the instrument; it has run on the machine and correctly refused to answer without a bridge.
- **The rule-1 safety fix compiles against real ATAS but has never executed.** It is a guard on a
  path that only runs inside ATAS.
- ~~The protocol cannot distinguish "not proven yet" from "the round trip failed."~~ **Fixed
  2026-08-27** — `BridgeHello` carries the two counters and `probe atas` reports rather than infers.
  The counters have still never been produced by the real adapter inside ATAS.
- ~~`AtasConnector` discards a mismatched hello, so nothing in the app can name the version.~~
  **Fixed 2026-08-27** — the identity is kept in `AtasConnector.Incompatible` and reaches the status
  row; the claims are still refused. Never seen on screen.
- **The Windows GUI has still not been looked at.** Captures cannot photograph an RDP desktop —
  `win-shot.sh` lands on the physical console, which is a different desktop, and captures blank.
  Every visual judgement remains one made against the app on macOS.
- **The system-check screen's two-line rows were not seen rendering.** The change is structurally
  identical to the `Numbered` helper, which was watched rendering correctly on the Welcome screen;
  the screen itself auto-advances on a healthy machine and was not forced open. NOT VERIFIED.
- **Neither AI runtime is `Verified = true`.** That flag means proven on Windows. Both mechanisms are
  now proven on macOS; the Windows-only halves are listed above, in the macOS section.
- **The installer is unsigned.** Every user will see "Windows protected your PC" and must click
  More info → Run anyway. On a program that places trades, that wants a certificate.
- **Live money has never been touched.** Correct for this stage.

## Current blockers

1. **Somebody signed in at the test machine.** On 2026-08-27 it was reachable and idle with **no
   desktop session at all** (`tools/win-state.sh`: `desktop: no active session`), and the remaining
   steps are GUI work inside ATAS that no amount of SSH reaches. Everything that could be done
   without a desktop has been: the ATAS-enabled build is installed and verified in place.
2. **The bridge inside ATAS.** Everything left is downstream of it: the two capability verdicts, the
   five in-ATAS steps, and any claim that an order reached a broker. ATAS itself is no longer a
   blocker — it is installed, signed in and running, and as of 2026-08-27 the installed TradeAgent
   finally carries a bridge that ATAS could load.
3. **A broker connection**, before any claim that an order reached one.
4. **A code-signing certificate**, before this goes to anyone who did not build it.

## Next integration target

1. Install the bridge from the app, then the five in-ATAS steps — noting that ATAS does not watch the
   Strategies folder, so the strategy list must be refreshed before the add-on appears.
2. Run `probe atas` and record what `SupportsClientOrderId` and `SupportsOrderHistory` actually say.
   Expect `SupportsClientOrderId = false` on a fresh session: that is rule 1 behaving correctly, not
   a fault. Place one paper order and run it again — that is now a path that can complete, which it
   was not before 2026-08-27.
3. Walk the whole setup journey on Windows and look at it, from the console rather than over RDP.
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
