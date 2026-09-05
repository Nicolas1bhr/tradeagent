# BUILD-STATUS

**Milestone: `LIVE_CONFIRM` is walked end to end through ATAS.** An AI session proposed an order, the
gateway parked it, a human approved it in the app, and it reached ATAS and came back with a broker
order id — then was cancelled and the book verified clean. Detail in the 2026-08-31 Windows section.

**Closed 2026-08-31, later session: an account nobody chose could be traded.** `PlaceAsync` resolves
the account through a helper that falls back to whichever one the platform lists first when nothing
has been chosen — fine for rendering a status screen, and it was reaching the broker. On a platform
carrying both a practice and a real-money account, list order decided whose money it was. The gate is
now in `TryAuthorizeExecution`. It became reachable the same day, because changing the platform after
setup has to clear the chosen account; it is in the section for that day.

The bridge runs inside ATAS, its reads work, and orders have been placed through it.
Both capability verdicts are now false **for known reasons rather than for want of looking**, which
is the difference between a gap and an answer:

- `SupportsOrderHistory` — false because `IIndicatorDataProvider.GetService<T>()` throws
  `NotSupportedException` for *every* type, including one reachable as a property on the same
  interface. Every cache route is dead. Shippable.
- `SupportsClientOrderId` — **TRUE, on evidence, since 2026-08-30.** An order was placed, ATAS was
  shut down, and the identifier was found again on an order in the restarted platform's own
  collection, alongside the broker id the dead run had recorded before it ended. The process doing
  the reading had constructed no `Order` at all, so the match cannot be our own object — which is
  exactly what made every earlier reading worthless. In-session the reading is still
  `proven-sameref` and still reports false; that too was confirmed on hardware, on two different
  connectors, so it is how ATAS's collection works rather than one backend's quirk.

**The fact the product waited on from the beginning is settled.** The identifier survives ATAS being
restarted. What that does *not* settle — and the verdict says so itself — is whether it ever reached
the broker: ATAS rebuilding the order from the broker's answer and ATAS rehydrating it from its own
store are indistinguishable from inside a chart strategy. Only the broker's own report separates them.

The trap in that route is recorded below, because the obvious implementation of it produces an
automatic `true` rather than a proof: after a restart every match is reference-distinct by
construction, so it needed a reading of its own.

`ReconciliationProvable` is false and `TradingGateway` refuses `LIVE_AUTONOMOUS`. That is correct.

**Closed 2026-08-29: the bridge pipe authenticated nobody.** A process that won the pipe name received
the bridge's connection and could place orders in ATAS around every operator control, while the
agent-facing pipe demanded an ACL and a token. Both halves now enforce — and the residual against a
same-user adversary is written down rather than claimed away. Detail in the 2026-08-29 section.

The product's two defining promises — *no terminal, ever* and *it installs what it needs itself* —
remain verified by running them on real Windows 11.

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

### Found 2026-09-01, NOT FIXED: every successful cancel strands its own request at DISPATCHING

Found while checking why the Dashboard had reported "Open orders / unconfirmed: 1 / 0" since the
`LIVE_CONFIRM` walk. The walk's own order is fine; the stranded record is the cancel request the
gateway creates for itself. Read out of the live database and the engineering log:

```
execution_request : lc-walk-001         PLACE   CANCELLED     <- correct
                    lc-walk-001-cancel  CANCEL  DISPATCHING   <- stranded
engineering_log   : already_settled  lc-walk-001-cancel  {"intended":"CANCELLED","actual":"DISPATCHING"}
```

`CancelAsync` sets `DISPATCHING`, calls `CancelOrderAsync` (succeeded — the broker order was cancelled
and the activity log says so), then calls `Settle(id, CANCELLED)`. `OrderStateMachine.Allowed[DISPATCHING]`
does not contain `CANCELLED`, so the transition is refused, `Settle`'s `ILLEGAL_STATE_TRANSITION`
catch logs `already_settled`, and the record never moves. Deterministic — it happens on every
successful cancel.

**Severity is bounded and was bounded by reading, not by feel.** `Open()` has exactly one production
caller — `StatusAsync`, filling `GatewayStatus.OpenRequests` — which is display only. Nothing gates on
it: `needs_reconciliation` is 0, `ExecutionTrustable` is untouched, trading is unaffected. It is a
dashboard asserting something untrue about the book, growing by one per cancel.

**Deliberately not fixed in this session.** The obvious repair widens the one table whose header says
it is the only place transitions are legal, and the second half of the defect is subtler and probably
more valuable: `Settle`'s catch exists for "somebody else already settled this" and it reported
`already_settled` about a record nothing had settled. It can distinguish the two — if the stored state
is still the `from` state, nothing raced and the table refused — and that conflation is what let this
hide. **The fix is now written out step by step** in `docs/RESUME-HERE.md`, work-queue task 3.

### Found 2026-09-01, NOT FIXED: on ATAS the first ambiguous order pauses trading for good

Found by checking whether a piece of advice in the handoff was actionable. It was not. Every link was
read in the source, not inferred:

1. An ambiguous outcome becomes `UNKNOWN` and is flagged for reconciliation — rule 3 working.
2. `ReconcileAsync` will not guess on a backend that cannot prove its own history, and says so:
   *"cannot prove order state; needs a human to look"*.
3. `ReconciliationProvable` is false on ATAS and stays false — it needs `SupportsOrderHistory`, which
   is false because `GetService<T>()` throws for every type. Settled, and not a gap.
4. `TryAuthorizeExecution` refuses while anything needs reconciliation (`TradingGateway.cs:238`).
5. `TradingGateway.ForceResolve` — the designed human override, "the one place a person asserts a fact
   the software could not prove" — **exists, is tested, and has no route into it.** It appears in the
   gateway and in one test, and nowhere else in the product.

**So on the platform this ships for, the first ambiguous order pauses trading permanently and there is
no in-product way to clear it** — and "edit the database" is a workaround the no-terminal rule
forbids. It is correctly absent from the agent pipe and the CLI, because operator authority is
in-process only; the missing route belongs in the app.

Not fixed here: it wants a Dashboard surface, a two-press confirmation worded as the assertion it is,
and a required note. Scoped in `docs/RESUME-HERE.md`, work-queue task 2, and it **gates the staged
live trial**.

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

## macOS only, 2026-08-29 — the test machine was offline for the whole session

`tailscale status` reported the test machine `offline, last seen 9h ago` at the start of the
session and again at the end. **Nothing in this section has been run against real ATAS**, and
`AtasStrategyAdapter.cs` is `<Compile Remove>`d on macOS, so the adapter edits below have not been
through a compiler at all. What was available was the half that compiles everywhere, and the reading
of code — which is where the session's two largest findings came from.

Baseline re-verified before anything was touched: **107/107 green**. At the end: **169/169** — 43 unit,
36 fault, 90 integration. Every block of new tests in this section was proven to bite by breaking its own
implementation and recording which test failed, because a test that passes against the broken version is
worth nothing.

### Rule 1 was reporting satisfied on the proof that had already been called worthless

The 2026-08-28 run measured `coid=proven-sameref` and recorded, correctly, that the match proves
nothing. The capability was a separate `bool` that latched on **any** match, so `Describe()` went on
reporting `SupportsClientOrderId = true` from it. The adapter's own comment said the wiring was
deferred *"One live reading first"*. That reading existed and was vacuous, so the deferred change is
now made: `SupportsClientOrderId` is `ProvesRoundTrip(proof)`, true for `Distinct` alone.

The separate bool is deleted rather than corrected. Two variables for one fact is how a capability
and the `coid=` token printed beside it come to disagree, and the live run had them disagreeing
already: the token said the match was worthless and the boolean said the capability held.

**The latch is the part that would have gone wrong quietly.** `ProveClientOrderId` opens with an
early return so it stops rescanning the book once the answer is final. Had `SameRef` kept setting it,
the reading this platform actually produces would have frozen the proof for the life of the process —
a genuinely `Distinct` match arriving later could never be observed — and **nothing would have looked
wrong**, because the diagnostic would go on truthfully printing `proven-sameref` forever. The latch
now means "nothing stronger can be observed", which is a different question from "the capability is
true", so they are separate predicates that happen to agree today.

A second hole was found in the same method: the latch check and the proof write are separate lock
acquisitions with a full enumeration between them, and it is called from `Place` on the pipe thread
and from the order-event fan on ATAS's. Two passes can both clear the latch, so a straggling `SameRef`
could overwrite a `Distinct` just established. The write is now monotonic.

The decision moved out of the ATAS-only file into `ClientOrderIdProof.cs`, which every machine
compiles — the predicate that gates autonomous live trading was sitting where no test on any machine
but the ATAS box could reach it. 26 cases now cover it, including the latch hazard by name.

### A wedged ATAS call could silence the bridge while the heartbeat said READY

`Block()` waited forever. `BridgeServer` awaits `HandleFrame` before reading the next frame, so one
call that never returns means **no further frame is ever read off the pipe** — including the
operator's cancel-all, which is how the book gets cleared. The heartbeat runs on its own task and
keeps beating throughout, so the connector goes on reporting `READY`. A wedged bridge that reports
healthy defeats the one check meant to catch it.

`AtasCall.Block` now carries a deadline, and expiry is emphatically **not** a rejection:
`WaitAsync` ends our wait and cannot recall a request already handed to the platform, so the order may
be resting at the broker. `AtasCallTimeoutException` is not derived from `AtasRejectedException` and
says the outcome is unknown and must be reconciled — rule 3, in the direction that costs a reconcile
rather than the direction that loses money.

Five seconds is **arithmetic, not a measurement**: `Place` costs the call plus `WaitFor(AckTimeout)`,
5 + 3 = 8, and `AtasConnector`'s RPC timeout is 10. Above about 6s the connector gives up before the
bridge answers and the bridge is still wedged when the next frame arrives.

`BridgeServer`'s `catch` was right about the shape the adapter throws today and wrong about the shape
a task-based path produces: `.Wait()` or `.Result` wraps a refusal in an `AggregateException` and the
bare catch would miss it, sending `rejected=false` for a definite broker "no". It now unwraps
single-fault wrappers only — several failures are ambiguous by definition.

**The tests were proven to bite.** Each of seven wrong implementations was applied to the real source
and the suite run: `.Wait()` instead of the awaiter, no timeout, a timeout turned into a rejection, the
call left unawaited, and three variants of the wire classifier. Every one failed at least one named
test. The two pre-existing rejection tests —
`A_definite_rejection_survives_the_crossing_as_a_rejection` and
`Losing_the_bridge_surfaces_as_indefinite_rather_than_as_a_rejection` — **did not appear in a single
failure list across all seven**, so their blindness to this change is measured rather than asserted.

### Step 3's premise was wrong, and the correction changes what the switch is for

`docs/RESUME-HERE.md` said switching to the `...Async` overloads "moves every refusal from thrown out
of the call to faulted task", and that rule 3's classification is built on the first shape.

**There is no `catch` in the adapter's write path at all.** Not one `AtasRejectedException` after
submission comes out of an order call; every one is manufactured from `_failures`, which is written
only by `OnFailurePayload`, fed only by ATAS's `OrderRegisterFailed` / `OrderCancelFailed` /
`OrderModifyFailed` events — a path the sync/async choice does not touch. So the switch does not move
the refusal path, and rule 3's classification is not what is at stake in it.

What is at stake is timing, and separately a hole the switch would close: **the new deadline covers
one of five write paths**. `AtasCall.Block` is reached only for `feed.RegisterOrderAsync`. The other
four writes are synchronous calls into ATAS that cannot be given a deadline from this side at all, so
if any of them blocks the pipe loop still stops and the heartbeat still reports `READY`. Flipping them
to the Async overloads would put all four under the deadline. That is an argument for the switch that
was not previously recorded.

Signatures, quoted from the dump rather than guessed — all four return plain `Task`, so the
"`false` means refused" hazard does not exist, and none takes a `CancellationToken`:

```
Task CancelOrderAsync(Order order, Boolean askConfirmation, Boolean checkOrderStates)
Task ClosePositionAsync(Position position, Boolean askConfirmation, Boolean checkOrderStates)
Task ModifyOrderAsync(Order order, Order newOrder, Boolean askConfirmation, Boolean checkOrderStates)
Task OpenOrderAsync(Order order, Boolean setDefaultQuantity, Boolean askConfirmation, Boolean checkOrderStates)
```

**NOT VERIFIED, and it is the gate on the switch: whether `OpenOrderAsync`'s task completes on
submission or only on broker acknowledgement.** If the latter, blocking on it puts `Place` past the
connector's 10s deadline and turns every order into UNKNOWN. Only the Windows machine can answer it.

**Correction to the record:** `RESUME-HERE` states the 2026-08-28 order "placed cleanly from the
bridge's pipe thread and returned in under two seconds". **That figure is not quotable from any
instrument in this repository** — nothing times the place call; the probe's only `Stopwatch` on that
path times the read-back. What the run proves is that `Place` returned inside the connector's 10s RPC
timeout without a rejection.

### The probe would have accused the bridge of a defect it does not have

Three verdicts in `tools/probe` were written when any match set the capability, so "the book shows
both ids and the bridge says false" could only mean something was broken. It is now the **correct**
reading on ATAS. Worst of the three: the disagreement branch sits above the `proven-sameref` branch
and returns first, so the accurate sameref explanation written directly beneath it was dead code on
precisely the run it was written for. Both verdict functions now take the `coid=` token, and the
harness's own order-book reading says outright that it is the weaker of the two here — object identity
is a thing the bridge can see and the order book cannot.

### The ATAS API dump existed only in a temp directory

Every ATAS identifier the bridge uses — 125 of them — was checked against a 6,581-line reflection dump
that lived nowhere but session scratchpads under `/private/tmp`, which macOS clears. There is no ATAS
NuGet package and no vendor documentation at that depth. It is now `docs/atas-api-8.0.14.397.txt`.
Public type and member names only; scanned before committing, and the password/secret hits are all
ATAS member names (`SecureString Secret`, `ILoginPasswordConnectorSettings`).

### SAFETY: the bridge pipe authenticated nobody — found, and closed

Found by reading, verified against both files before any fix was dispatched. **Not a regression: true
since the bridge existed.**

The agent-facing gateway pipe is defended twice — a Windows `PipeSecurity` limited to the current user,
and an `IpcToken` demanded on `Hello`, one chance per connection. The bridge pipe, the one that reaches
`IAtasAdapter.Place`, had neither:

```csharp
_pipeStream = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
```

**The attack is impersonation, not connection.** A pipe *name* is not access-controlled and the instance
limit is 1, so whichever process creates that name first owns it and the bridge inside ATAS connects to
whatever is listening — then takes orders from it, around the mode, the kill switch, the approvals, the
risk limits and the autonomy gate, all of which live in `TradingGateway`. `AcceptLoop` retried every
second after a drop, so a squatter needed one moment, not a boot race. `tools/probe` does exactly this
by design, so the technique was already proven in-repo.

**Both halves now enforce.** The bridge proves the pipe's owner *before* the hello, so an unproved peer
learns nothing — not the ATAS version, not the account id, not what the platform can prove. The
connector refuses an unproved hello the way it refuses an incompatible one: `_hello` stays null, so
`Capabilities` keeps reporting nothing supported.

**Refusing the hello alone would have been worth nothing, and this is the part that matters.**
`BridgeOps.Heartbeat` carries a whole `BridgeHello` — that is how a capability proved after the
handshake reaches the connector, added 2026-08-27 — and the branch assigned it unconditionally. A peer
that never says hello is never refused for saying one, so it could set both capabilities on a heartbeat
instead: **the same unlock, one frame to the left.** Guarded, with a test that names the property. The
event branch is guarded too, so an unproved peer cannot feed the gateway fabricated fills either.

`BridgeProtocolVersion` went 1 → 2. The wire contract genuinely changed, and it makes a stale bridge get
named by `IncompatibleBridge` rather than surfacing as an authentication failure.

**What it does not stop, stated plainly because an overclaimed boundary is worse than a documented
gap.** The adversary named in the threat model is the AI runtime, and `CliAgentRuntime` starts it with
`Process.Start` **as the same OS user**. It can read the secret file — which is why that file is
deliberately *not* DPAPI-protected: `CurrentUser` unprotects for the same user, so it would be ceremony.
The peer-image check is the only rule that can bite a same-user squatter, and it refuses anything under
the managed tools directory by a rule not derived from the recorded image, so a runtime that rewrote the
record to name itself is still refused. **That is tamper-evidence, not a boundary.** What the change
buys against that adversary is that a squat must now be targeted — read the secret, tamper with the
state directory, race the rewrite — rather than "open the name and send `place`".

Every refusal is a sentence reaching `StatusDetail`, and it says in as many words that this is **not**
trap 12, 7 or 24, because all three of those present as *no answer at all* and this is something
answering wrongly.

**NOT VERIFIED: every Windows-only path** — `GetNamedPipeServerProcessId`, `QueryFullProcessImageName`,
the `PipeSecurity` ACL — has never executed. The *rules* they feed are tested directly; the kernel calls
supplying their arguments are not. **NOT VERIFIED: any of it against the real ATAS bridge.**

### SAFETY: two routes to a `Distinct` the adapter manufactured against itself — closed

Making `Distinct` the gate turned two existing behaviours into ways of manufacturing the proof, both on
the honest path with no attacker involved:

- **`Modify`'s clone.** `order.Clone()` copies `Comment`, so the replacement is an object this adapter
  constructed carrying our client order id while `_submitted` holds the *original*. A read-back asking
  only "is this the instance I submitted" sees a different object with our identifier on it.
- **`ClosePosition`** writes our identifier by hand onto an order ATAS created — safe only because that
  id never enters `_submitted`. Incidental, not designed.

`AdapterTouchedOrders` holds every order object the adapter constructed or labelled, by reference
identity. All three registration sites record the object **before** it becomes visible to anyone else:
the order-event fan runs on ATAS's thread and can reach the read-back the instant an object appears
there.

**The trim is the part that would have gone wrong quietly.** A bare `Clear()` leaves a forgotten clone
looking exactly like an order of ATAS's own, and the next read-back records `Distinct` — **trimming
would have manufactured the proof the type exists to prevent.** It latches instead, and refuses every
proof from that point. The permanence costs almost nothing: the proof latches on the first `Distinct`,
so only a session that has already answered "not proven" 4096 times can reach the cap.

`StopBridge` had the same unbounded wait removed from the write path — `DisposeAsync` awaits the frame
loop, so a wedged write blocked strategy teardown on ATAS's own thread forever.

**NOT VERIFIED: whether ATAS's collection ever holds the `Modify` clone.** The dump carries public
members only. The guard deliberately does not depend on the answer — that was the point.

### The rule-1 restart proof: designed, with the trap named

The cheapest real proof is a fresh ATAS session — anything surviving a process restart cannot be our
object. The obstacle is that after a restart `_submitted` is empty and `ProveClientOrderId` refuses any
id not in it, which is the deliberate 2026-08-27 safety fix.

**The trap, and it is the finding: relaxing that guard does not give a proof, it gives an automatic
`true`.** After a restart this process has constructed no `Order` at all, so *every* match is
reference-distinct by construction — and `Distinct` now sets the capability. A reading true by
construction, dressed as a measurement, is precisely the vacuity `SameRef` was invented to expose,
re-imported one level up.

So the mechanism needs a reading of its own (`CrossSession`), with the latch following it rather than
`Distinct`, and a durable write-ahead record of which ids this product submitted — written *before* the
order exists, by a process that is gone by the time it is read, and carrying the broker id that
process saw ATAS assign. Designed, not built: it cannot be exercised while the machine is offline.

**And what it would prove is bounded.** A cross-session match cannot distinguish ATAS rebuilding the
order from the *broker's* answer on reconnect from ATAS rehydrating it from its own local store. Both
survive a restart, both look identical from inside a chart strategy. Only the broker's own report
separates them, and that is not a source the software can read at runtime during an outage.

## Verified on real Windows 11 hardware, 2026-08-30

Nine commits of adapter, protocol and security changes had never been through a compiler. All of it
was built, deployed into ATAS, and run.

### The adapter compiles against real ATAS — all of it, first attempt

```
  TradeAgent.AtasBridge -> C:\ta\repo\src\TradeAgent.AtasBridge\bin\Release\net10.0-windows\TradeAgent.AtasBridge.dll
Build succeeded.
    5 Warning(s)
    0 Error(s)
```

The five warnings are the four known `CS0618` obsolete order calls and one pre-existing `MSB3277`
WindowsBase unification. Identity asserted on the **deployed** artifact rather than the built one
(trap 8), checking type names as ASCII and string literals as UTF-16 (trap 27):

```
  AtasStrategyAdapter        PRESENT      proven-sameref   PRESENT
  AdapterTouchedOrders       PRESENT      proven-distinct  PRESENT
  AtasCallTimeoutException   PRESENT
  BridgePipeAuth             PRESENT
```

### The bridge pipe authentication works against real ATAS, in both directions

```
CONNECTOR AUTH        : OK — the bridge proved itself to AtasConnector as well
                        peer image, as Windows reports it: C:\Program Files (x86)\ATAS Platform\OFT.Platform.exe
```

That second line matters more than the first. **The Windows-only peer-identity path executed** —
`GetNamedPipeClientProcessId` and `QueryFullProcessImageName` — and named the platform correctly. It
was NOT VERIFIED as recently as the previous session, on the grounds that the rule it feeds was tested
but the kernel calls supplying its argument were not. They are now.

`proto=2` throughout, so the version bump is live and the connector and bridge agree on it.

### RULE 1: a vacuous match no longer sets the capability — measured, live

The whole point of the previous session, confirmed on hardware. Simulated account `CRYPTO5EB41`, one
buy limit priced ~10% under the bid and rounded down so it could not fill, read back, then cancelled:

```
CLIENT ORDER ID       : TA-PROBE-20260830113311
THE ORDER             : BUY LIMIT 1 BTCUSDT @ 70191  TIF=Day  on CRYPTO5EB41
PLACE CALL            : RETURNED — ATAS took the order without a definite refusal.
ORDERS BEFORE         : 0            ORDERS AFTER        : 1
CARRIES OUR ID        : YES — client_order_id = TA-PROBE-20260830113311
CARRIES A BROKER ID   : YES — connector_order_id = 12007695
SUBMITTED WITH AN ID  : 1            READ-BACKS PERFORMED : 3
SupportsClientOrderId : false   AFTER the attempt — this is the reading that counts.
ROUND TRIP, MEASURED  : proven-sameref — ATAS handed back THE VERY OBJECT we submitted.
                        THE PROOF IS VACUOUS
```

**On 2026-08-28 that identical situation reported `SupportsClientOrderId : true`.** It now reports
false, off the same evidence, because the evidence is worthless. That is the fix working where it
matters.

Two further things this run establishes:

- **The same-reference behaviour is not one connector's quirk.** The 2026-08-28 order went through the
  ATAS Sim connector on ES; this one went through a Binance crypto-sim connector on BTCUSDT. Both
  return the submitted instance, so this is how ATAS's order collection works generally.
- **The order path survived nine commits of change.** Place, read back three times, cancel, and a
  re-read showing `0 order(s) in the collection, 0 carrying this run's id`. `AdapterTouchedOrders` was
  live throughout and produced no false `Distinct`.

### RULE 1 IS PROVEN — across a process restart, and this proof is not vacuous

**The single fact this product has waited on since the beginning, answered.**

Half 1 placed a resting order and deliberately left it, writing a witness record **before** the order
was submitted:

```
CLIENT ORDER ID       : TA-PROBE-20260830120255
THE ORDER             : BUY LIMIT 1 BTCUSDT @ 70155  TIF=Day  on CRYPTO5EB41
CARRIES A BROKER ID   : YES — connector_order_id = 12007918
ROUND TRIP, MEASURED  : proven-sameref — ATAS handed back AN OBJECT THIS ADAPTER TOUCHED.
WITNESS RECORD        : session:bccb57cf,records:1,prior:0,io:ok
```

ATAS was then closed — saving the workspace — and confirmed gone from the process table, so the run
that placed that order, and the `Order` instance it constructed, ceased to exist. ATAS was relaunched,
signed in, and the strategy re-activated (it restores **stopped**, trap 24). Half 2 places nothing:

```
BRIDGE SESSION        : 1ce7ec65
RECORD SESSION        : bccb57cf
ORDERS IN LIVE BOOK   : 1
ORDER SURVIVED        : YES — an order with broker id 12007918 is in the book
IDENTIFIER SURVIVED   : YES — an order carries client_order_id = TA-PROBE-20260830120255

{"connector_order_id":"12007918","client_order_id":"TA-PROBE-20260830120255",
 "account_id":"CRYPTO5EB41","symbol":"BTCUSDT","side":"Buy","type":"Limit","quantity":1,
 "filled_quantity":0,"limit_price":70155,"state":"WORKING","at":"2026-08-30T10:02:57.3913483+02:00"}

RULE 1                : PROVEN ACROSS A PROCESS RESTART. THIS IS THE ANSWER.

atas=8.0.14.397 | SupportsClientOrderId=true | coid=proven-crosssession | coid-restart=proof
```

**Why this one is not vacuous, where every previous one was.** The reading that made 2026-08-28
worthless was that ATAS handed back the very object we submitted, so the comment matched by
construction. Here **this process constructed no `Order` at all** — it placed nothing, and its
`_submitted` map is empty. There is no object of ours for the collection to be holding. The claim
"this product submitted this identifier" was written to disk before an order existed to fit it to, by
a process that had ended before anything read it, and it is matched against **the half we did not
choose**: the broker id ATAS assigned, recorded by that dead run, required to be equal on the order
found now.

**What it still does not prove, and this bound is real.** A cross-session match cannot separate ATAS
rebuilding the order from *the broker's* answer on reconnect, from ATAS rehydrating it out of its own
local store. All three survive a restart and are indistinguishable from inside a chart strategy. So:
**the identifier demonstrably survives ATAS being restarted**, which is what reconciliation after a
dropped connection needs. Whether it ever reached the broker is a different question, and only the
broker's own report answers it. That distinction is printed in the verdict itself, not just recorded
here.

**Autonomy is still refused, and that is correct.** `ReconciliationProvable` is
`SupportsClientOrderId && SupportsOrderHistory`; the second is false for a known reason
(`GetService<T>` throws for every type). One of the two gates is now open on evidence. The other is
shut on an answer.

**The book was left clean**, verified from a separate run afterwards:

```
orders=0 strategyorders=0 mytrades=0 portfolio=CRYPTO5EB41 security=BTCUSDT position=0
coid=proven-crosssession   witness=session:1ce7ec65,records:1,prior:1,io:ok
```

### The quote guard refused to place, correctly, on a closed market

The first attempt was on ES, and the machine's clock read `Sunday 2026-08-30 11:25 +02:00` — CME is
shut, and the chart's last bar was Friday 22:55.

```
QUOTE (raw)           : {"symbol":"ES","at":"0001-01-01T00:00:00+00:00"}
REFUSED TO PLACE      : THE QUOTE CARRIES NO USABLE BID.
                        NOTHING WAS SUBMITTED.
```

`quote=none(no-tick)`. This is the same signature as the 2026-08-28 defect where the bridge had never
seen a price — but there it was a wiring fault and here it is the market being closed, and the guard
refuses either way rather than pricing an order off the ask or the last trade.

Moving to a 24/7 instrument is what made the rest of this section possible on a Sunday.

### A second feed answers the DateTimeKind question differently

```
ES  (dxFeed, 15-min delayed) : quote=... kind=unspecified
BTCUSDT (Binance)            : quote=event(bid=77980.0,ask=77990.0,age=-0s,kind=utc)
```

The dxFeed path stamps `Unspecified` and was measured on 2026-08-28 to be UTC underneath. **This feed
stamps `Utc` explicitly.** So `MarketDataArg.Time`'s kind is per-feed, not per-platform, and code that
inferred a fixed convention from the ES measurement alone would have been generalising from one feed.
The conversion handles both. `age=-0s` — a real-time feed, marginally ahead of this machine's clock,
comfortably inside the 60s future-stamp guard.

### The UI agent survives a reboot from its new location

NOT VERIFIED since 2026-08-28, when the move to `C:\ta\agent\bin` was made *after* the reboot that
would have tested it. Free measurement on a machine that had just been woken:

```
  uptime           : 0d 00:04
  session          : 1  interactive=True
  automation       : WORKS - read the tree, find and invoke elements
  capture          : WORKS
```

### A defect in the probe, and a commit message that overclaimed

The run above ended with the probe accusing the bridge of a fault it does not have:

```
RULE 1  : THE EVIDENCE IS PRESENT AND THE BRIDGE STILL SAYS false — INVESTIGATE.
```

The disagreement branch is still tested **before** the `proven-sameref` branch, so the accurate
explanation written directly beneath it is unreachable on exactly the run it was written for.

**Commit `1b352d6`'s message states that this reordering was the fix, and the diff does not contain
it.** Only the two verdict functions received their sameref cases; this block was missed, and the
message was written from intent rather than from the diff. Recorded here because the honest record is
the point of this file, and a commit message that describes work it did not do is the same failure
mode as a status claim that was never run.

### What is still not answered

**NOT VERIFIED: whether ATAS carries the identifier onto anything this adapter did not write.** The
reading is `proven-sameref` on two different connectors now. The cross-session mechanism that would
settle it is being built; nothing here settles it.

**NOT VERIFIED: the four synchronous order calls off the GUI thread under load, and whether
`OpenOrderAsync` completes on submission or acknowledgement.** Untouched today.

**NOT VERIFIED: the app's own UI on Windows.** Still nobody has looked at TradeAgent itself here, only
at ATAS.

## Verified on real Windows 11 hardware, 2026-08-31 — LIVE_CONFIRM, end to end, through ATAS

**The milestone the product was built around is walked.** An AI-side session proposed an order, the
gateway refused it and parked it, a human approved it in the app, and it reached ATAS and came back
with a broker order id. No terminal was shown at any point.

### The walk

Mode set to "Real, ask me first" and real-money trading switched on in the app (two presses each), on
the provably simulated `CRYPTO5EB41` account (`is_simulated: true`, Binance crypto-sim, USDT 100,000).

The agent proposes, as a non-operator session over the pipe:

```
== the AI proposes: buy 1 BTCUSDT limit 70000 (well below market, so it rests) ==
{ "ok": false, "error": {
    "code": "APPROVAL_REQUIRED",
    "message": "request lc-walk-001 is waiting for your approval",
    "user_message": "The AI is asking permission to place an order.",
    "repair": "Approve or decline it in TradeAgent." } }
exit code: 1
```

The app raised the banner "The AI is asking permission — 1 order waiting" in the shell chrome, with
"Review the request"; the Dashboard showed `Buy 1 BTCUSDT at 70000 / asked at 15:57 / Approve · Decline`.
Approve is itself two-press ("Confirm: place this order"). After confirming:

```
request_id        : lc-walk-001
agent_session_id  : agent-liveconfirm-walk     <- proposed by a non-operator session
connector_id      : atas
account_id        : CRYPTO5EB41
client_order_id   : TA-lc-walk-001
created_at        : 2026-08-31T13:57:18        <- when the AI asked
dispatched_at     : 2026-08-31T13:58:59        <- only after the human approved
state             : WORKING
connector_order_id: 12021602                   <- ATAS's own order id
mode              : LIVE_CONFIRM
```

ATAS's own Trading Activity panel showed `CRYPTO5EB41 / BTCUSDT / FLAT / Long 1,00`, independently of
our record. The order was then cancelled and the book verified clean from a separate run:
`orders: []`, position `quantity: 0`, request `CANCELLED`, `filled_quantity: 0`,
`needs_reconciliation: false`.

The product's own activity log, which is what the account owner reads:

```
15:55  Trading mode set to LIVE_CONFIRM
15:56  Real-money trading switched ON by the user
15:57  AI is asking permission to Buy 1 BTCUSDT
15:57  AI order refused: ... (request lc-walk-001 is waiting for your approval)
15:58  You approved Buy 1 BTCUSDT
15:59  Buy 1 BTCUSDT -> WORKING
16:00  Cancelled order 12021602
```

**Precision about what is new.** The same log carries an earlier walk at 03:30 — `Filled 1 ES at
109.74` — against the **built-in simulator**. So the approval flow itself had been exercised before.
What had never been done, and was done today, is the whole path through the **ATAS bridge to a real
platform**: agent → gateway → risk limits → approval → bridge → ATAS → broker order id → cancel.

### SAFETY-ADJACENT DEFECT: the AI's only route to the gateway could not start — found and fixed

`trade.exe` on this machine threw on every invocation:

```
Unhandled exception. System.IO.FileNotFoundException: Could not load file or assembly
'TradeAgent.Core, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null'.
```

`ToolDeployer.EnsureTradeCli` copied the launcher and its three side-cars (`trade.dll`,
`trade.runtimeconfig.json`, `trade.deps.json`) and **none of the seven assemblies the CLI loads**. The
deployed folder held one DLL where a working CLI needs eight. Its own comment said a
framework-dependent build needs side-car files — and then stopped one step short of the referenced
assemblies.

**The severity is bounded, and the bound matters.** `packaging/build.ps1` publishes the CLI
`--self-contained -p:PublishSingleFile=true`, so in a shipped installer `trade.exe` carries everything
and the copy is a no-op. **The shipped product was not broken.** What was broken is every
*non-packaged* run — a developer build, a CI run, or a machine running the app out of `bin/Release`,
which is precisely the configuration anyone would use to test the agent path. It is why this survived
to today: the one path that exercises it is the one nobody had run.

**And the health row lied about it.** `Health.Set(Components.TradeCli, File.Exists(...) ? READY : FAILED)`
asked only whether a file of that name existed, so the Dashboard reported `trade CLI: ready` about a
binary that could not start. That is trap 9 again: a check that passes on a thing that cannot work.

Fixed both: the deployer now copies the assemblies named in the CLI's own `deps.json` (read from the
manifest, so a new package reference cannot silently reintroduce it), and `ToolDeployer.TradeCliReady`
reports FAILED naming the missing files. Verified on the machine — the bin folder went from 1 DLL to
8, and `trade accounts --json` returned `CRYPTO5EB41 ... "is_simulated": true`.

Four tests, and three of them were proven to bite:

| Break | Result |
|---|---|
| Copy only the launcher trio again | `The_cli_is_deployed_with_the_assemblies_it_actually_loads` and `A_cli_that_cannot_start_does_not_report_ready` FAILED |
| Make the readiness check ignore missing assemblies | `A_cli_that_cannot_start_does_not_report_ready` FAILED |

### The schema 1 → 2 migration ran on the real Windows database

Not a fresh test database — the machine's own, with prior orders and settings in it:

```
schema_version : ('2',)
has material   : True
has note tbl   : True
```

### TradeAgent's own UI, seen on Windows for the first time

Every screen visited rendered correctly at the default window size: Chat, Dashboard, Inbox, Safety,
Activity. The nav, the header (mode pill, platform, account, AI-trading dot), the kill switch, the
approval banner, the two-press confirmations and the activity log all read as intended, and no text
was clipped or truncated on any of them.

**The half-pressed confirmation survived two background refresh ticks** — the Approve button stayed
armed as "Confirm: place this order" across a screenshot and a re-query. That is the build-once,
update-in-place rule doing exactly the job the convention exists for.

**NOT VERIFIED: the setup journey on Windows.** Onboarding is complete on this machine and there is no
route back into it (see below), so the wizard screens were not seen.
**NOT VERIFIED: the bridge-refusal sentence on the status row.** The bridge was healthy all session, so
the ~450-character refusal string never rendered.
**NOT VERIFIED: the Inbox page with real content on Windows, and every drag-and-drop path.** The page
was seen empty only, and nothing was dragged onto it.

### PRODUCT GAP: platform and account can only be chosen during setup

`SwitchConnectorAsync` is called from `OnboardingView` and nowhere else; `SelectedAccountId` is
likewise written only there. `MainWindow` enters the wizard on `if (!_host.Onboarding.IsComplete())`,
so once setup finishes **there is no route back into it**. A user who set up against the practice
simulator and later wants ATAS — or who wants a different account on the same platform — cannot do
either from the running app.

This blocked the walk. Both values were changed directly in the database to get past it, which is a
harness action and not a product path, and is recorded as such. **Not fixed today**; it wants a
deliberate design decision about where platform and account live in the shell.

### Health rows that do not reflect reality

`ATAS process` and `ATAS bridge` both read `unknown` for the whole session, while the bridge was
demonstrably connected, serving live quotes and carrying an order to the broker. Nothing outside
"Check everything" sets those rows. Not fixed; recorded.

### Corrections to the ATAS recipe in `docs/RESUME-HERE.md`

Two of them, and both matter because the recipe currently tells the next person to click raw
coordinates:

- **`PART_ActivateButton` exists, is enabled, and is findable without expanding the row.** The
  2026-08-30 note says "there is no `PART_ActivateButton` step" and gives `click --x 1004 --y 641`.
  Today `find --query 'Activ'` returned it directly and `invoke --ref` started the strategy. The
  coordinate click is unnecessary and is exactly what trap 37 warns against.
- **`find --window '<title>'` can kill the UI agent, not merely fail.** `find --window 'Authorization'
  --query Connect` timed out at 90 s and left the agent dead (`NOT RUNNING`, stale heartbeat); the
  same query without `--window` answered immediately. The older note recorded this as answering "no
  visible window matching", which is a much milder failure than the one seen today.

### Tests

```
Passed!  - Failed: 0, Passed:  36, Skipped: 0, Total:  36  TradeAgent.FaultTests.dll
Passed!  - Failed: 0, Passed:  58, Skipped: 0, Total:  58  TradeAgent.UnitTests.dll
Passed!  - Failed: 0, Passed: 130, Skipped: 0, Total: 130  TradeAgent.IntegrationTests.dll
```

224 tests. Windows `dotnet build TradeAgent.sln -c Release` clean; bridge rebuilt with
`-p:AtasBridgeBuild=true` and redeployed, with the deployed artifact asserted rather than the built one
(`MaterialScanner in deployed Core: True`, `AtasStrategyAdapter in deployed dll: True`).

## macOS only, 2026-08-31 — the AI inbox and the material ledger

Scope addition: the account owner can hand the AI programs, documents and data to experiment with.
Built on the dev Mac in one session; **the Windows machine was not touched, and nothing below speaks
for it.**

### The ledger, which is the half that could not be added later

A dropbox with no provenance is a dump within a fortnight, and the files that arrive before the
record exists can never be accounted for afterwards. So the record shipped first and the folder was
built around it. Two tables, deliberately not one:

- `material` — what TradeAgent **observed**: relative path, origin, size, SHA-256 it computed itself,
  first seen, last seen, removed. Written only by the scanner. The agent cannot write or edit a row.
- `material_note` — what somebody **claimed**: ran it, used it, derived this from that. Written by the
  agent over the authenticated pipe, and stored apart so it can never alter an observation.

A row is a file version, not a path. Database schema 1 → 2, purely additive.

### Verified by running it

Full suite, after every change:

```
Passed!  - Failed: 0, Passed:  36, Skipped: 0, Total:  36  TradeAgent.FaultTests.dll
Passed!  - Failed: 0, Passed:  54, Skipped: 0, Total:  54  TradeAgent.UnitTests.dll
Passed!  - Failed: 0, Passed: 130, Skipped: 0, Total: 130  TradeAgent.IntegrationTests.dll
```

215 tests, up from 204: 11 unit tests on the ledger and scanner, 5 integration tests driving the two
new operations over a real named pipe with a real handshake.

**The tests were proven to bite**, by breaking the implementation and recording which test failed:

| Break | Result |
|---|---|
| Stop skipping `node_modules` / `obj` / `.git` | `Package_and_build_directories_are_not_tracked` FAILED |
| Let a changed file overwrite its predecessor instead of versioning | `Replacing_a_file_keeps_the_version_it_replaced` FAILED |
| Remove the truncated-pass guard entirely | `A_scan_that_ran_out_of_budget_never_reports_a_file_as_removed` FAILED |

**And one break did NOT bite, which is recorded because it changes what the code may claim.** The
guard reads `if (complete && !truncated)`. Removing only `!truncated` breaks nothing: `complete`
already covers every case the tests produce. `!truncated` is belt-and-braces for a later origin whose
walk comes back empty after an earlier one ran out of budget — a state no test currently reaches. It
is kept, and the comment on it says plainly that it is not covered. **Do not read that line as tested.**

### Seen rendering, on macOS

The Inbox page was looked at with three files in the inbox, one file produced by the agent, and three
seeded notes. Screenshots taken and read. What was confirmed by eye:

- The list separates "you gave this to the AI" from "the AI made this", inbox first.
- An `.exe` carries a `runs` badge; sizes, arrival times, path and short hash all render on two lines.
- The notes section renders as its own block under "What the AI says it did", with the derivation
  chain visible as `04bb01a6c112 ← 25931da0389d`.
- The drop zone, "Choose files…" and "Open folder" render and are laid out on the theme's tokens.

**NOT VERIFIED: any of the interaction.** No file was ever dragged onto the window, the file picker
was never opened, and "Open folder" was never pressed — the macOS harness can screenshot this app but
cannot click it. The drop path, the picker path, the copy, the collision-suffixing and the
immediate-rescan are **compiled and unexercised**. This is the largest untested surface added today.

**NOT VERIFIED: the whole feature on Windows**, which is where it will be used. Nobody has seen
TradeAgent's own UI on Windows at all — the inbox inherits that gap rather than creating it.

**NOT VERIFIED: the agent actually using it.** No AI runtime has been pointed at `trade material` and
asked to record its work. The commands answer correctly over the pipe and appear in
`trade schema --json`; whether an agent reads the AGENTS.md section and complies is unmeasured, and
is the thing that decides whether the notes half of the ledger has any content at all.

### A latent argument-parsing defect, found and fixed on the way

`trade`'s positional list was built by filtering out anything starting with `--`, which also kept
every flag's *value* as a positional. Harmless while no command read past its second positional;
wrong the moment one takes a flag between positionals, which `trade material derived <sha> --from
<sha> <text>` does. Positionals are now parsed by walking the argument list and skipping each flag's
value. Existing commands read only positions 0 and 1 and are unaffected.

## Verified on real Windows 11 hardware, 2026-08-31, later session — the two gaps the walk exposed

The previous session walked `LIVE_CONFIRM` end to end and recorded two gaps it hit on the way. Both
are closed. Neither needed a screenshot to prove: the app answers `trade status` over its own pipe,
so the health rows can be quoted as data rather than described from a picture — which is fortunate,
because the machine's RDP session is disconnected and renders nothing (trap 19).

### Gap 1 — the two ATAS health rows were never written by anything

`Components.AtasProcess` and `Components.AtasBridge` were declared in `Components.All` from the first
build and **no code anywhere ever called `Health.Set` for either**. The previous session recorded the
symptom ("`unknown` for a whole session in which the bridge was demonstrably serving quotes"); this is
the cause, found by grepping every `Components.` reference: every other component has a writer, those
two had none.

Reproduced first, on the machine where it was seen, through the still-running pre-fix build:

```
BEFORE   (Windows, mode=LIVE_CONFIRM, connector=atas, bridge live)
  ATAS process           UNKNOWN
  ATAS bridge            UNKNOWN
  Trading connection     READY
  Account                READY     CRYPTO5EB41
  Market data            READY
```

Then pushed, rebuilt Release on the machine, asserted the artifact carries `AtasHealthReporter` and
`SettingsPage` as ASCII metadata names (trap 8, trap 27), relaunched, and asked again:

```
AFTER    (same machine, same ATAS session, same bridge)
  ATAS process           READY     running · 8.0.14.397
  ATAS bridge            READY     connected · bridge 8.0.14, protocol 2
  Trading connection     READY
  Account                READY     CRYPTO5EB41
  Market data            READY
```

The rows are deliberately not a second opinion on `Trading connection`. That row answers "can the
gateway talk to the backend"; these answer the question a user actually has when it says no — **which
half is missing.** Three states that were indistinguishable on screen now read differently:

```
not installed in ATAS — press Install bridge on the Checks page
installed — waiting for ATAS to start
installed, but the strategy is not started on a chart in ATAS
```

The third is trap 24 — ATAS restores a chart strategy **stopped** after every restart — which until
now looked identical to a bridge that failed to load.

On the practice simulator both rows read `UNKNOWN — not in use — you are on the practice simulator`,
verified against the running app on macOS. `UNKNOWN` and not `READY` is the honest state: nothing was
checked, because nothing needed to be. Detection is skipped entirely there rather than enumerating
processes every five seconds for somebody who has no ATAS.

Nine unit tests pin the decision table (`tests/TradeAgent.UnitTests/AtasHealthTests.cs`), including the
regression that started this: a reporter pass must leave neither row with nothing to say.

**A real defect found on the way:** `AtasInstallation.Detect` called `Process.GetProcessesByName` and
dropped every `Process` object it was handed. Harmless while nothing called it on a timer — and this
change puts it on a five-second one. `IsRunning` now disposes them.

### Gap 2 — platform and account could only be chosen during setup

`SwitchConnectorAsync` and `SelectedAccountId` were written only by `OnboardingView`, and the wizard is
only entered while setup is unfinished. After setup there was no way to change either; the previous
session worked around it by editing the database by hand. Setup meanwhile tells the user
*"You can switch later"* while asking the first of them.

There is now a **Settings** page in the shell (`src/TradeAgent.App/SettingsView.cs`), between Safety and
Activity. Read on Windows through UI Automation — the first time TradeAgent's own UI has been read on
that machine at all — with ATAS selected and `CRYPTO5EB41` chosen:

```
Text    'Platform in use'                     Text   'Account in use'
Button  'Use ATAS'                enabled=False     <- already the platform in use
Button  'Use the practice simulator' enabled=True
Button  'Use this account'        enabled=False     <- already the chosen account
Button  'Look again'              enabled=True
Text    'IN USE'      Text 'SIMULATION'      Text 'CRYPTO5EB41'
```

Widening risk is two-press and narrowing it is one: moving to ATAS and choosing a **real-money**
account arm first (`Ui.Confirm`); moving back to the simulator and choosing a simulated account do
not. Account cards carry onboarding's own `SIMULATION` / `REAL MONEY` pill treatment verbatim.

**Note for whoever changes ATAS accounts:** the list offers exactly one — the portfolio the bridge's
chart is bound to. `ChartStrategy.Connector` is null (trap 13), so the bridge can only see its own
chart's portfolio. Changing ATAS account means moving the strategy to a chart on the other account,
not picking from this list.

### A safety hole the new page exposed, closed the same session

Switching platform must clear `SelectedAccountId` — an id issued by one platform does not exist on the
other, and carrying it across makes every later lookup a miss on a perfectly healthy connection. That
clearing made a previously unreachable state reachable: **no account chosen, on a live platform.**

`TradingGateway.AccountAsync` falls back to `GetAccountsAsync().FirstOrDefault()` when nothing is
chosen, so a status screen can render before anything is configured. `PlaceAsync` goes through the
same call — so the fallback reached the broker. **On a platform carrying both a practice and a
real-money account, list order decided whose money it was, and nobody had asked the owner.**

`TryAuthorizeExecution` now refuses with `ACCOUNT_NOT_FOUND` when nothing has been chosen, and the
`Account` health row reports `DEGRADED — no account chosen yet` instead of presenting the fallback as
a healthy chosen account. The emergency controls are deliberately outside `AuthorizeOrThrow` and stay
outside it: taking authority away can never be blocked by a missing configuration choice. Both facts
are pinned by tests (`PolicyGateTests.An_account_nobody_chose_is_not_traded_even_though_one_is_available`,
`The_emergency_controls_still_work_with_no_account_chosen`).

Seen in the running app on macOS, in the header, with nothing chosen:

```
AI paused — no account has been chosen — choose one on the Settings page
```

### `ITradingManager.Orders` and `ChartStrategy.Orders` are not the same collection

Answered out of captures that were already on the machine and had never been read.
`probe-half2.txt` and `probe-clean.txt` report `orders=1 strategyorders=0` with one resting order
live; `probe-verify.txt` reports `orders=0 strategyorders=0` after the cancel, so the 1 was tracking
the real order. Both counts are built inside one `SurfaceReport` call — a single instant — and a
shared list cannot report two lengths at once.

**NOT VERIFIED, and the captures cannot answer it:** whether an order placed by *this* strategy
instance in *this* session ever appears in `ChartStrategy.Orders`. Every surface reading ever taken
was at the hello, before anything was placed, so `strategyorders=0` has never been observed in the one
situation that would give it meaning. The probe now takes the reading again after the place and prints
`ORDER COLLECTIONS   before: … after: …`, so the next hardware run closes it.
`LiveOrders`' reference de-duplication stays either way — defensive on this evidence, and what it
prevents is `FilledOf` double-counting a partial fill into a FILLED.

### The place path, measured on hardware: the synchronous call completes on SUBMISSION

Two orders, on the simulated `CRYPTO5EB41` Binance crypto-sim account, through the real bridge:

```
run 1   place=sync;call=16777us;atreturn=None/noid;settled=131666us;gap=114889us;now=Active/id
        broker id 12024794
run 2   place=sync;call=531us;  atreturn=None/noid;settled=125074us;gap=124543us;now=Active/id
        broker id 12024817
```

**`ITradingManager.OpenOrder` returns before the broker has acknowledged anything.** On the warm run
it returned in **0.53 ms**, with the order still at `State=None` and **no `Order.Id` assigned**.
Acknowledgement — the state change and the broker id — arrived **124.5 ms later**. Run 1's 16.8 ms
call is the same call cold; the shape of the reading is identical in both.

**Acknowledgement latency on this venue is ~120 ms, so submission and acknowledgement ARE separable
here.** That was not a foregone conclusion and it is the reading that had to come first: a platform
that acknowledges in under a millisecond cannot distinguish the two answers at all, and a fast
`OpenOrderAsync` completion on such a venue would have been evidence for neither. The probe prints
the verdict itself (`SEPARABLE` / `NOT SEPARABLE`, thresholded at 20 ms).

**NOT VERIFIED — what `ITradingManager.OpenOrderAsync`'s task waits for.** That still needs a
submission through the async overload, which needs a probe-only route into `Place`; it was not
smuggled into this change, because a second way to submit an order inside `Place` is exactly where a
rule-3 misclassification would hide. The mechanism is designed in `docs/RESUME-HERE.md`, work-queue
task 1.

**What the measurement changes about the decision, and it is not what was expected.** The recorded
fear was that blocking on `OpenOrderAsync` would put `Place` past `CallTimeout` and turn every order
into UNKNOWN. At ~120 ms against a 5 s `CallTimeout` that cannot happen on this venue whichever
answer is true — a 40× margin. And `Place` *already* waits for acknowledgement, in
`WaitFor(AckTimeout)`, on exactly the condition the async task would be waiting for; so the switch
moves where that time is spent rather than adding to it.

The real difference is subtler and is the thing to weigh when the switch is made: **today a slow
acknowledgement ends in `WaitFor` giving up and returning the order in whatever state it is really
in — no exception. After the switch it ends in `AtasCallTimeoutException`, i.e. UNKNOWN.** That is
arguably more correct under rule 3, but it is a behaviour change on the money path and it deserves
its own change and its own reasoning, not a footnote to a timing measurement.

### `ChartStrategy.Orders` is empty even for orders this strategy placed

The open half of the collections question, closed. Both runs above:

```
ORDER COLLECTIONS   before: orders=0 strategyorders=0   after: orders=1 strategyorders=0
```

The earlier reading (2026-08-30) showed the two counts differing across a restart, which proved they
are not the same collection but left open whether an order placed by *this* strategy instance in
*this* session would appear in `ChartStrategy.Orders`. It does not. `strategyorders` has now been 0
in every reading ever taken, including immediately after this strategy successfully placed an order
that `ITradingManager.Orders` counted.

So `LiveOrders()` reading both collections is not redundancy — `ITradingManager.Orders` is the one
that carries anything, and the reference de-duplication is defensive. **Both stay.** "It has never
contained anything" is not "it can never contain anything", and the cost of reading it is one
enumeration.

Book verified clean from a separate run after both orders:
`orders=0 strategyorders=0 mytrades=0 position=0`.

### ATAS will not restart on a disconnected RDP session

Found by hitting it, and it cost the middle of the session. ATAS was closed to swap the bridge DLL —
the normal redeploy step — and would not come back: it signs in, opens its main window, starts
building the workspace's chart panels, and dies ~40 s later. Deterministic, reproduced twice, and
there is no TradeAgent frame anywhere in the stack:

```
Faulting application name: OFT.Platform.exe   Faulting module: coreclr.dll   Exception: 0xc0000005
   at OpenTK.Windowing.GraphicsLibraryFramework.GLFWNative.glfwGetVideoMode(Monitor*)
   at OpenTK.Windowing.Desktop.NativeWindow..ctor(NativeWindowSettings)
   at OpenTK.WinForms.GLControl.CreateNativeWindow(GLControlSettings)
```

ATAS draws its charts through an OpenGL control; GLFW cannot enumerate a video mode in a session with
no rendering surface, and the null it returns is dereferenced. An existing GL context survives a
disconnect — which is why ATAS had been running for days in that state — but a new one cannot be
made. **This refines trap 19 rather than repeating it:** a disconnected session costs TradeAgent, UI
Automation and the bridge only their rendering, and costs ATAS its ability to start at all.

Recorded as trap 43 with the event-log command that identifies it, because it presents as *"the
bridge DLL you just deployed broke ATAS"* — it happens on the first launch after a redeploy, exactly
when the workspace and its strategies load.

**The fix, and it needed no reboot.** Session 1 was a disconnected RDP session; the physical console
(session 2) was connected and idle at the logon screen. `tscon 1 /dest:console` moved the session onto
the console, and `tools/win-state.sh` went from `capture : NO` to `capture : WORKS`. ATAS then
launched, signed in and stayed up past 120 s where it had died at 40 s twice. That is the confirmation
of the diagnosis as well as the repair.

### The ATAS-down health branch, verified by accident

The outage proved on hardware the branch that this session's other fix exists to expose, which a
healthy machine could not have shown:

```
ATAS process           DEGRADED  not running — press Open ATAS on the Dashboard
ATAS bridge            FAILED    installed — waiting for ATAS to start
Trading connection     FAILED
Account                UNKNOWN   no connection
```

`Trading connection: FAILED` with an empty detail is the entire diagnosis a user got before. The two
rows above it now say which half is missing and what to press.

### Seen on Windows, and the looking is what found it: the health details were being trimmed away

`Ui.StatusRow` laid out `16,180,*` in a 340px card and trimmed the detail with an ellipsis. The two
ATAS rows added this session — whose entire purpose is to say which half of the trading chain is
missing — rendered as:

```
ATAS process    running · 8.0....
ATAS bridge     connected · ...
```

Correct, and unreadable, which is the worse of the two failures: a wrong row invites a second look
and a truncated one does not. The dashboard's bridge-refusal detail is ~450 characters and would have
displayed as approximately nothing — the previous handoff wondered whether it truncated, and this is
the answer. The detail now wraps and the component column is 140. Verified on Windows:

```
ATAS process    running · 8.0.14.397
ATAS bridge     connected · bridge
                8.0.14, protocol 2
```

Nothing but looking at it would have found this. It is the argument for work-queue task 4 in one
screenshot.

### Trap 21 came back, with TradeAgent as the victim

`win-push.sh` deletes `C:\ta\repo\src` before unpacking, and TradeAgent now runs from inside it.
Windows refuses to delete a running `.exe` and removes everything beside it, so a push would have left
a half-deleted install that still looked built. The push now refuses before deleting anything.
Verified against the real machine with the app running:

```
  RUNNING FROM THE REPO: TradeAgent - C:\ta\repo\src\TradeAgent.App\bin\Release\net10.0\TradeAgent.exe
REFUSING TO PUSH. ...
win-push exit = 1        # and C:\ta\repo\src was intact afterwards
```

### Tests

`dotnet test TradeAgent.sln` — **235 passed, 0 failed** (38 fault, 67 unit, 130 integration), up from
224. Solution build clean, 0 warnings.

## Verified on real Windows 11 hardware, 2026-09-01 — `OpenOrderAsync` answered, and three gaps in the escape hatch

**Context that bounds everything below, stated by the owner and not previously written down: the
Windows machine's ATAS is signed in with a FREE ATAS account and has NO BROKER attached.** Both
accounts are simulated. Every latency here is ATAS's own simulator answering, not a venue.
Conclusions about *API semantics* transfer off this machine; **the numbers do not.**

### `ITradingManager.OpenOrderAsync` completes on ACKNOWLEDGEMENT, not on submission

The last open sub-question on the place path. Answered by an A/B on the same account minutes apart,
through a probe-only route (`--via-async-overload`) built for the purpose. Both readings quoted from
the run:

```
control  : PLACE TIMING : sync;call=16904us;atreturn=None/noid;settled=129433us;gap=112529us;now=Active/id
           ROUTE ACTUALLY USED : sync — as asked.
reading  : PLACE TIMING : asyncoverload;call=108500us;atreturn=Active/id;settled=108504us;gap=4us;now=Active/id
           ROUTE ACTUALLY USED : asyncoverload — as asked.
           READING — STATE : ACKNOWLEDGEMENT. atreturn=Active/id
```

**The decisive witness is categorical, not a duration, which is why it survives the no-broker bound.**
The synchronous call returned `atreturn=None/noid` — the order had no state and no broker id yet. The
async overload returned `atreturn=Active/id` — the order **already carried both**. The task did not
complete until ATAS had acknowledged. `gap=4us` on the async run is the signature of that, not a
failed measurement: settlement had already happened when the call returned, so there is nothing left
to wait for. The probe prints `ACK LATENCY : NOT SEPARABLE` for that run and it is correct to; the
control run is what establishes separability (`gap=112529us`), and it is why the control is required.

Book verified clean from a **separate** run after each placement: `ORDERS IN LIVE BOOK : 0`.

**What this changes about flipping the four obsolete synchronous call sites — and it argues for more
caution, not less.** The four `CS0618` warnings are still present and the call sites were deliberately
NOT flipped. The prior reasoning was that ~120 ms against a 5 s `CallTimeout` is a 40x margin, so
blocking on the async call could not turn orders into UNKNOWN. **That margin is a simulator
measurement and is not a property of the product.** Now that the async task is known to wait for
acknowledgement, the flip moves the failure mode from "`WaitFor` gives up and returns the order in
whatever state it truly is, with no exception" to "`AtasCallTimeoutException` — UNKNOWN", and the only
thing standing between a slow venue and that outcome is a number obtained from a simulator with no
broker behind it. **NOT VERIFIED and unverifiable here: what a real broker's acknowledgement latency
is.** The flip still deserves its own change and its own reasoning.

The measurement route cannot be reached by the product, and the audit is one line
(`src/TradeAgent.AtasBridge/AtasStrategyAdapter.cs:1261`):

```csharp
public OrderInfo Place(PlaceOrderCommand cmd) => Place(cmd, PlaceRoute.Default);
```

`PlaceRoute` is `internal` to the bridge assembly, and `grep` over `TradeAgent.Gateway`,
`TradeAgent.App` and `TradeAgent.ConnectorSdk` for `PlaceViaAsyncOverload` returns **nothing** — the
gateway holds an `ITradingConnector`, which cannot name the route at all. `LoopbackAtasAdapter`
**refuses** the call rather than producing a timing, because an in-memory adapter would emit this
process's scheduler latency wearing ATAS's name.

### The bridge was rebuilt against real ATAS, and the deployed artifact asserted

`AtasStrategyAdapter.cs` is `<Compile Remove>`d off Windows, so every change an agent made to it was
**unverified by any compiler** until this build. It compiled with **0 errors**. The four `CS0618`
obsolete warnings on `OpenOrder`/`ModifyOrder`/`CancelOrder`/`ClosePosition` are still there, which is
the evidence that the live call sites were not flipped; `OpenOrderAsync` raises no CS0618.

### Trap 27 has a hole, and it would send you rebuilding a correct DLL

Trap 27 says to check assembly **string literals** as UTF-16. That is right and incomplete:
**decoding the file as UTF-16 from offset 0 only finds literals that begin at an EVEN byte offset.**
Measured on the freshly deployed bridge:

```
asyncoverload              even=False odd=True   PRESENT=True
place-via-async-overload   even=True  odd=False  PRESENT=True
connector                  even=False odd=True   PRESENT=True
proven-sameref             even=True  odd=False  PRESENT=True
```

Roughly half of all literals read as **absent** on a perfectly good build. Trap 27's own worked
example, `proven-sameref`, happens to land even — which is exactly why the gap survived being written
down. Its stated failure mode is "that reads as *the build did not take*, and the natural next move is
to rebuild and redeploy something that was already correct", and this is precisely the path there.
**Check both alignments.**

### Every successful cancel stranded its own request at DISPATCHING — fixed

`CancelAsync` reached `Settle(id, CANCELLED)` after a confirmed broker cancel, but
`Allowed[DISPATCHING]` had no `CANCELLED` entry, so `Settle` caught `ILLEGAL_STATE_TRANSITION`, filed
it as `already_settled` and returned the record unchanged. Deterministic: one permanently "open"
request per cancel. Display-only — `Open()`'s single production caller is `StatusAsync`, filling
`GatewayStatus.OpenRequests`, and no gate or risk check reads it.

Two changes, and the second is the one that let the first hide. `CANCELLED` was added to
`Allowed[DISPATCHING]`, and `Settle` now **distinguishes the two failures that arrive at the same
catch**: the table refusing `from -> to` is a defect in the caller, while the store's CAS check
failing is a genuine race. They are separable without parsing a message — if the stored state is
still `DISPATCHING`, nothing raced. A table refusal is now logged `illegal_settle` at `error`
severity and is never filed as `already_settled`. It does **not** rethrow: this runs on a write path
that has already reached the broker, and reporting failure for an operation that succeeded is the
wrong direction.

**NOT VERIFIED on hardware:** the fix is pinned by tests, not by driving a live cancel through the
gateway on the Windows machine. **And nothing backfills the existing stranded record.**
`lc-walk-001-cancel` is real data on that machine and `trade status` still reports
`open_requests: 1  unreconciled_requests: 0` with `trade orders` returning `[]`. That is the honest
expected result, not a regression. It was deliberately not hand-edited — resisting exactly that is
what created the record.

### Three gaps in the escape hatch, two of them opened or exposed by the fix above

**1. `Decline` had no guard of its own, and the widening removed the one it was relying on.**
`Decline` called `_requests.Transition(requestId, stored.State, CANCELLED)` with no state check —
unlike `ApproveAsync` twelve lines above it, which refuses anything not `AWAITING_APPROVAL`. The state
table was its only protection: before this change, declining a `DISPATCHING` record threw. After it,
the same call **succeeds and writes CANCELLED over an order that may be live at the broker** — the
software asserting an outcome nobody obtained. Unreachable from today's UI, which offers Decline on
pending-approval rows only, but "unreachable today" is not a safety property. `Decline` now refuses
anything not `CREATED` or `AWAITING_APPROVAL`.

**2. `ForceResolve` threw on the records it most needed to open.** The human override for a request no
machine can settle. Five links, each read rather than assumed:

- `MarkNeedsReconciliation` is a bare `UPDATE ... SET needs_reconciliation=1` and **never touches the
  state** (`src/TradeAgent.Core/Db/Stores.cs:127`).
- `NeedingReconciliation()` is `Query("needs_reconciliation=1")` — **no state constraint**.
- `SettleUnknown`'s catch calls it when the event stream already settled a record mid-dispatch, so a
  record can be **`FILLED` and flagged at once**.
- `TryAuthorizeExecution` counts the flag, not the state, so that record **pauses trading**.
- `ForceResolve` computed `CanTransition(FILLED, anything)` = false, fell through to
  `Transition(id, FILLED, RECONCILING)`, and the table refused it. **The only escape hatch threw.**

So the feature as briefed would have shipped a button that throws on a reachable class of row.
`ForceResolve` now clears the flag via `ClearReconciliation` when the person confirms the state the
record already holds, and **refuses** to rewrite one settled terminal outcome as a different one —
that is the stream and the platform disagreeing, and overwriting it would erase the only account of
what the software was told.

**3. `ForceResolve` clears the flag but does not by itself resume trading.** `TryAuthorizeExecution`
has a second gate: `ExecutionTrustable` requires `Components.ExecutionCapability` to be `READY`, and
**only `RefreshHealthAsync` recomputes it**. Without a refresh the owner presses the button and
watches "AI paused" sit there until the next 5-second tick — a button that looks dead. The Dashboard
card calls `RefreshHealthAsync` immediately after, and a test pins that the override alone leaves
`TRADING_PERMISSION_UNAVAILABLE`. Two smaller consistency gaps went with it: `ForceResolve` was the
only mutator on the class that never fired `StateChanged`, and `ReconcileAsync`'s `pending.Count == 0`
path returned early **without** clearing the health row its own non-empty path clears — so "reconcile
until clean" could not actually finish for any caller not also refreshing health.

### The reconciliation override now has a route into it

A Dashboard card, visible only when something is flagged, built once and updated in place with a
rebuild gated on the request-id signature. It lists what is known per request — instrument, side,
quantity, our client order id, the broker id if there is one, when it was dispatched, and what the
last reconcile attempt said — with the facts wrapping rather than ellipsizing. Two-press via
`Ui.Confirm`, worded as the assertion it is ("Confirm: I checked in ATAS and no such order exists"),
above an amber paragraph saying the owner is asserting something TradeAgent could not check and that
AI trading resumes on their word. The note `ForceResolve` already takes is **required**: the buttons
stay disabled until one is typed, and editing it disarms a half-pressed confirmation, so a
confirmation armed against one sentence cannot be completed against another.

**Deliberately NOT reachable from the agent-facing pipe.** No `trade resolve` command was added and
`GatewayPipeServer` was not touched. Operator authority is in-process only; an agent that wants more
permission must have nowhere to ask.

Only **FILLED** and **CANCELLED** are offered, and that is derived rather than chosen: they are the
only outcomes reachable from every state a flagged record can hold. `WORKING` is the obvious third
answer and the easiest to check in ATAS, and it is unreachable from `WORKING`, `PARTIALLY_FILLED` and
`CANCEL_PENDING` — i.e. it would throw exactly where it is most likely to be true. The card tells the
owner to cancel it in ATAS first instead, after which "no order exists" is literally true.

**Verified with eyes on macOS** against records seeded by driving a real failed dispatch, so the
client order id, parameters and error text are what the product actually writes.
**NOT VERIFIED on Windows:** the card is correctly *absent* there (`unreconciled_requests: 0`), and it
has never been seen rendering on that machine. `find --query 'COULD NOT CONFIRM'` returns 0 matches,
which is the correct behaviour and is not the same as having watched it work.

### The header asserted "real money" whenever the platform had not answered

Found by looking at the running app, which is the only thing that finds this class of defect.
`AtasConnector.Capabilities` reports an all-false capability set while its handshake is null —
deliberately, so the trading gates fail closed (`TradingGateway.cs:274` leans on exactly that). But
`Ui.PlatformLabel` read that same `IsPaper == false` as a positive assertion and rendered
**"ATAS · real money"**, on screen beside a `Practice` badge and a simulated account: three labels
contradicting each other about the only fact that matters. On the Windows machine, in
`LIVE_CONFIRM`, it read "Real, ask me first · ATAS · real money · CRYPTO5EB41" — on a simulated
account with no broker attached.

Over-warning is not the safe direction here, it is a different failure: a header that cries "real
money" through every practice session is one the owner has stopped reading by the day it is true.
`PlatformLabel` now takes the platform's answered-ness and says "not connected" when it has none.
**Verified on hardware in both reachable states:**

```
ATAS closed     : find 'not connected' -> 1 hit  "ATAS \u00b7 not connected"
                  find 'real money'    -> 0 hits
ATAS connected  : "ATAS \u00b7 simulation"
```

The third state — a genuine real-money account — **cannot be produced on this machine**, because
there is no broker attached. NOT VERIFIED, and not verifiable here.

**No automated test.** `TradeAgent.UnitTests` deliberately does not reference the Avalonia app
project, and pulling Avalonia into the test host to pin one string is the wrong trade. The evidence
is the two hardware readings above.

### Task 4, part one — every page has now been seen on Windows, and looking found three more

Dashboard, Safety, Settings, Inbox, Checks, Activity and Chat were each opened and photographed on
the Windows machine. Most of it renders correctly: the ATAS bridge health detail **wraps** rather
than ellipsizing (the 2026-08-31 truncation fix holding), the three `unknown` health rows carry grey
dots and not green ones, the Checks output wraps multi-line paragraphs cleanly, and the Inbox page
renders the ledger's central distinction in the owner's own language — "Measured by TradeAgent" over
one list, "Reported by the AI as it worked. Its account, not a measurement." over the other.

Three defects that only looking could have found. All three fixed and re-photographed on Windows.

**1. The Activity page showed a time on every row and no date anywhere.** The log spans days; entries
from three different ones ran together with nothing between them, so on the single screen whose whole
job is "a plain-language record of what TradeAgent, the AI and you have done", `16:00 Cancelled order
12021602` could have been an hour ago or last week. It now carries day separators — "Today",
"Yesterday", then a weekday and date, with the year only when it is not the current one. A separator
rather than a date per row, so the narrow mono time column that makes the list scannable survives.
Verified: the `LIVE_CONFIRM` walk now sits unambiguously under **Yesterday**.

**2. A disabled stepper was the most prominent control on the Safety page.** `Ui.NumberField`'s
down-stepper on a limit already at its minimum rendered as a raised, pale, rounded box while its
enabled neighbours were flat and dark — on a dark theme, *lighter* reads as hovered or active, so the
one control the owner cannot press drew the eye first.

The cause is trap 4 for the fourth time, with a new twist worth writing down: **Avalonia's
`OfType<T>()` is an EXACT-type selector, so the global `Button:disabled` rule never matched a
`RepeatButton` at all.** The steppers were not styled by any rule in `Theme.cs`; Fluent's own disabled
paint won by default. Fixed by giving `RepeatButton` its resting, hover, pressed and disabled fills
explicitly, through the same `Fill(... .Template().OfType<ContentPresenter>())` route the button
variants already use, because setting `Background` on the control alone does nothing.

**Residue, honestly recorded: the disabled cell's right-hand corner is still rounded** where the
enabled rows are square, so something in Fluent's disabled template still paints a radius the new
rule does not reach. Cosmetic, no longer misleading, not chased further.

**3. Three Checks rows named a problem without stating one.** `Agent runtime`, `Agent process` and
`Workspace` carry no `Detail` until the AI has been started, and the renderer omitted the whole
`": {Detail}"` clause when it was blank — producing `• Agent runtime` followed straight by
"what to do: See the activity history for what happened." Worse than the Dashboard, which at least
prints "unknown". They now fall back to plain words for the state itself: `• Agent runtime: not
checked yet`.

**This is the wording half only.** The proper fix is still a `NOT_APPLICABLE` health state so a
component nobody is using stops being counted as a fault, which touches `Doctor.AllHealthy` and the
`trade status` wire. Still in the queue, and the rail still reads "3 parts not checked yet".

**NOT VERIFIED, and still open in task 4:** the setup journey end to end has never been watched on
Windows; the Inbox drop handler, file picker, copy, collision suffix and rescan have never been
exercised **on any platform**; the bridge-refusal sentence has not been seen rendering in a refusing
state; and the reconciliation card has still never been seen on Windows, because nothing there is
flagged (`find --query 'COULD NOT CONFIRM'` returns 0 matches, which is correct behaviour and is not
the same as having watched it work).

**No automated tests were added for any of these three.** The app project has no test project by
design, and all three are visual. The evidence is the before/after photographs on the Windows machine.

### Task 4, part two — material went through the ledger on Windows, and the measurement is provably right

The first time anything has been handed to the Inbox on **any** platform. A file was created with a
hash computed independently *before* it was copied, so the ledger's measurement could be checked
rather than trusted:

```
sha256 computed before the copy : 8354f46600833f65d133ea25da500657982e36b277ad4ef465815c158a866b7c
sha256 the ledger recorded      : 8354f46600833f65d133ea25da500657982e36b277ad4ef465815c158a866b7c
{"path": "inbox/strategy-notes.txt", "origin": "inbox", "size_bytes": 93, "runnable": false}
```

The row renders on the Windows Inbox page in the owner's language — `strategy-notes.txt` /
"you gave this to the AI · 93 B · arrived 1 Sep 01:59" / path and short sha in mono — and **"What the
AI says it did" correctly stayed empty**, which is the two-table separation surviving all the way to
the screen. Removing the file dropped it from both the ledger and the page. Nothing was left behind:
inbox empty, ledger `count: 0`.

**A timing fact that would otherwise cost a session, twice over: the inbox scan is NOT immediate.**
A screenshot taken 12 seconds after a file lands shows "Nothing handed over yet" — and that is not a
defect. It appeared on the next pass, and a removal likewise took longer than 25 seconds to clear.
Both times the honest move was to check the CLI (`trade material list`, which reads the same store)
before believing the screen, and both times the "defect" evaporated. **Do not report an empty Inbox
page until `trade material list` agrees with it.**

**Still NOT VERIFIED, and now with a reason rather than an omission: the drop handler and the file
picker's copy path have never run.** The picker itself opens correctly — the dialog appears with the
right title, "Choose files to hand to the AI", and the main window goes disabled behind it (trap 22's
modal signature). But it could not be driven from here: `setvalue` through the ValuePattern does not
commit, the dialog exposes no `IDOK` (`automationId` 1) to invoke, and `type`/`key` never reached it
because the harness cannot bring a native modal to the FOREGROUND — so the keystrokes went somewhere
unknown. The attempt was abandoned rather than escalated, because stray keystrokes with a live
trading platform on the same desktop is not a risk worth a screenshot. The dialog was cancelled and
the book, positions and ATAS were all confirmed undisturbed afterwards.

### Tests

`dotnet test TradeAgent.sln` — **256 passed, 0 failed** (45 fault, 67 unit, 144 integration), up from
235. Solution build clean.

Every new test was **proven to bite** by breaking its implementation and recording which test failed:

| Break | Test that failed |
|---|---|
| Remove `CANCELLED` from `Allowed[DISPATCHING]` | `A_successful_cancel_settles_its_own_request_instead_of_stranding_it` |
| Same | `The_table_lets_a_dispatching_cancel_reach_cancelled` |
| `illegal_settle` logs `already_settled` instead | `A_settle_the_table_forbids_is_recorded_as_a_defect_rather_than_a_race` |
| Remove the `Decline` state guard | `Decline_refuses_an_order_that_has_already_been_sent` |
| Remove `ForceResolve`'s already-in-that-state branch | `A_flagged_record_the_stream_already_settled_can_still_be_resolved` |
| Remove the terminal-conflict refusal | `Force_resolve_will_not_rewrite_a_settled_outcome_as_a_different_one` |
| Remove the card's `RefreshHealthAsync` | all 4 cases of `Health_stays_paused_until_it_is_refreshed` |
| Force-resolve to an unreachable target | all 4 cases, `ForceResolve` threw |

### A process defect worth recording, because it destroyed work

Three agents ran in parallel against one working tree. One of them ran `git stash push` to measure a
pre-change test baseline, which **swept up the other two agents' uncommitted work**. Both recovered
their own paths and the tree was verified intact afterwards, byte for byte — but nothing about that
was guaranteed. **A whole-tree `git stash`/`reset` must not be reachable by an agent that does not own
the whole tree.** The existing rule was "do not repeat: two actors in one file"; this is the same
lesson one level out, and the file-ownership boundaries in the briefs did not cover it because a
stash names no files at all.

## 2026-09-01, later session — TradeAgent updates itself from GitHub (macOS, plus the installer half on Windows hardware)

**What was built.** The app asks GitHub for the newest release of its own repository, and when one is
newer than the running build it lights a strip under the header: *"TradeAgent 0.2.0 is available ·
84.1 MB. You are running 0.1.0."* with **What's new**, **Install update** (two-press) and **Later**.
The same offer, plus the version numbers, the last-checked time and an on/off switch for the
automatic asking, is a card on the Settings page. Pressing install downloads the release's
`TradeAgent-Setup-x64.exe`, checks it against the `SHA256SUMS.txt` published beside it, starts Setup
with `/SILENT /NORESTART /SUPPRESSMSGBOXES /relaunch=1` and closes TradeAgent; `TradeAgent.iss` gained
a second `[Run]` entry that starts the new build afterwards, because the existing one is
`skipifsilent` and a silent install would otherwise end with the application simply gone.

**Three properties it was built to have, and they are the feature rather than decoration:**

- **It never installs anything on its own.** The background check lights a banner and nothing else.
  Both routes to installing are two-press, and the armed label says what the second press interrupts
  — "Confirm: close TradeAgent and install 0.2.0, 1 order still working".
- **It is not reachable from the agent.** `UpdateService` is owned by `AppHost` beside the gateway and
  the kill switch. `GatewayPipeServer` was not touched and no `trade` verb was added, so the AI can
  neither check, download, nor replace its own supervisor.
- **It says what the checksum proves.** The manifest comes from the same release as the installer, so
  it catches a truncated or corrupted download and nothing else. The card says so in as many words:
  *"which proves the file arrived intact — not who signed it."* The installer is still unsigned; that
  remains blocker 1.

### Verified

**The whole check path, against real GitHub, seen on screen.** With the update source pointed at a
stand-in repository that actually publishes a Windows installer
(`TRADEAGENT_UPDATE_REPO=notepad-plus-plus/notepad-plus-plus`,
`TRADEAGENT_UPDATE_ASSET='.*Installer\.exe$'`), the running app rendered:

```
TradeAgent 8.9.8 is available · 6.5 MB. You are running 0.1.0.     What's new  [Install update]  Later
```

and the Settings card showed `This version 0.1.0`, `Newest published version 8.9.8`,
`Automatic checks  on — once at startup, then every six hours`, `Last checked 1 September, 12:44.`
That is a real HTTP call to `api.github.com`, a real release document, our own parse of it, and our
own two surfaces rendering the result — not a fixture.

**A layout defect the screenshot found, and the fix was seen too.** The four buttons on the Settings
card ran off the right edge of it, and would have run further off once the two-step button armed and
started saying its whole sentence. `Ui.Wrap` was added for exactly this — a row that wraps instead of
clipping — and the card was then photographed with the button rendering its widest (armed) wording:
`Confirm: close TradeAgent and install 8.9.8` wrapped onto its own line, nothing clipped.

**Tests.** 40 new cases, aimed at the four ways an updater does harm rather than at the happy path:
offering something older than what is running, offering a release whose installer never uploaded,
running a file that failed its checksum, and installing without being asked.

```
Passed!  - Failed:     0, Passed:    45, Skipped:     0, Total:    45 - TradeAgent.FaultTests.dll
Passed!  - Failed:     0, Passed:   107, Skipped:     0, Total:   107 - TradeAgent.UnitTests.dll
Passed!  - Failed:     0, Passed:   144, Skipped:     0, Total:   144 - TradeAgent.IntegrationTests.dll
```

296 total, up from 256. `dotnet build TradeAgent.sln` — `0 Warning(s) 0 Error(s)`.

### Verified on real Windows 11 hardware — the installer half

**`packaging/TradeAgent.iss` compiles, `[Code]` section and all.** Copied to the test machine and
built with the same Inno Setup the packaging script uses:

```
Reading [Code] section
Parsing [Run] section, line 110
Parsing [Run] section, line 117
Compiling [Code] section
Successful compile (2,329 sec). Resulting Setup program filename is:
C:\ta\isscheck\out\TradeAgent-Setup-x64.exe
EXIT CODE: 0
```

Both `[Run]` entries parse, and the Pascal in `[Code]` compiles rather than merely looking right.

**`/relaunch=1` starts the new build, and nothing else does.** A throwaway derivative of the same
script — different `AppId`, different name, its own folder in `%LOCALAPPDATA%`, so it could not touch
the real install — was built around a stub program and installed **from the interactive session**,
twice:

```
WITH /relaunch=1 : 1 process(es) started by Setup
                   pid 20448  session=1  C:\Users\Nicolas\AppData\Local\TradeAgentRelaunchTest\TradeAgent.exe
WITHOUT /relaunch : 0 process(es) started by Setup
run entries in the two logs:
  with    /relaunch=1 : 1
  without /relaunch   : 0
```

Setup's own log is the second witness: one `-- Run entry --` with the relaunch flag, none without it.
So the new entry fires exactly when the updater asks and never on an ordinary silent install, and the
existing `skipifsilent` entry is not double-firing.

Everything was removed afterwards — install folder, uninstall registry entry, build folder — and the
real TradeAgent and ATAS were still running, untouched, at the end.

**Two false negatives worth recording, because they cost twenty minutes and would cost them again.**
The first two stub programs were `winver.exe` and `charmap.exe` copied out of `System32` and renamed.
Both exit immediately when run from anywhere else — MUI resources live in `System32\en-US\*.mui` —
so Setup was launching them correctly and the measurement said "0 processes". **A copied System32 GUI
program is not a test fixture.** The stub that answered the question was three lines of C# compiled on
the machine with `Add-Type -OutputType ConsoleApplication`, which depends on nothing.

### Verified on the real Windows machine — the app itself, not just the parts

The tree was pushed to the test machine, the app rebuilt in Release (`0 Warning(s) 0 Error(s)`) and
run, and the Updates card was looked at with eyes on Windows:

```
This version              0.1.0
Newest published version  could not be checked
Automatic checks          on — once at startup, then every six hours

TradeAgent could not check for a newer version — GitHub did not answer, or nothing has been
published yet. Nothing changed. Last checked 1 September, 13:53.

[Check for updates]  [Turn off automatic checks]
Releases come from github.com/Nicolas1bhr/tradeagent. The download is checked against the checksum
published beside it, which proves the file arrived intact — not who signed it.
```

**That is exactly the state a user has today**, and the check behind it was a real call to
`api.github.com` made from that machine by pressing the button. **Install update** and **What's new**
are correctly absent, no banner appears, and nothing lands in the red error strip — a repository with
no releases is not an error, and the sentence says which of the three causes it might be rather than
asserting one.

**The bridge reconnected to the rebuilt app**, which is what proves the pipe-buffer change did not
break the deployed add-on:

```
ATAS process             READY      running · 8.0.14.397
ATAS bridge              READY      connected · bridge 8.0.14, protocol 2
Trading connection       READY
```

Be precise about what that covers: the **TradeAgent-side** pipe change (the 8 KiB buffer) is in this
build and is what the deployed bridge just connected through. The `BridgeServer` changes ship inside
the bridge DLL, which is still the older deployed one — those are covered by the 146 integration
tests on this machine, not by this connection.

**CI is green on all three platforms for the first time in five commits, and the `package` job that
its red had been blocking ran and produced an installer** — the modified `TradeAgent.iss`, compiled
on a clean Windows runner rather than only on the test machine:

```
Compiler engine version: Inno Setup 6.7.1
Successful compile (107.938 sec). Resulting Setup program filename is:
D:\a\tradeagent\tradeagent\artifacts\TradeAgent-Setup-x64.exe
   version           0.1.0
   staged files      289 files, 405.9 MB
   ATAS adapter      ABSENT  - this build CANNOT trade through ATAS
   installer         artifacts\TradeAgent-Setup-x64.exe  (112.1 MB)
Done. Artifacts in artifacts
```

The ABSENT line is correct and is why that artifact is labelled `NO-ATAS-ADAPTER` and must not be
published as a release.

### v0.1.0 is published, and the check was watched to read it

The installer was built on the test machine **with the ATAS adapter in it** — the artifact CI cannot
produce — and published as the repository's first release:

```
   ATAS adapter      PRESENT - AtasStrategyAdapter is compiled into the bridge assembly
   installer         artifacts\TradeAgent-Setup-x64.exe  (112.2 MB)
f243bdcc5906e99a2e43dbe0ac517780a6597a71d0d3913871433e909fcc0a1b  artifacts/TradeAgent-Setup-x64.exe
```

The file was transferred and hashed again on the other machine before it was uploaded, and the two
readings agree, so what is published is the file that was built. The release as the updater sees it:

```
tag       : v0.1.0 | draft: False | prerelease: False
asset     : SHA256SUMS.txt                        497 bytes
asset     : TradeAgent-Setup-x64.exe      117,641,311 bytes
```

**Then the app read it, on Windows, without being asked.** TradeAgent was restarted on the test
machine and its startup check ran against the real release:

```
This version              0.1.0
Newest published version  0.1.0 — you have the newest one
Automatic checks          on — once at startup, then every six hours
Last checked 1 September, 14:18.
```

No banner, no Install update button, and the note is muted rather than amber — the healthy state,
reached from the live GitHub answer rather than from a fixture. All six health rows stayed READY
across the restart, bridge included.

**One precision about what the tag points at.** The staged build was compiled on the test machine
from the tree as pushed there, which differs from the tagged commit `88df7da` in exactly two files —
`BUILD-STATUS.md` and `docs/RESUME-HERE.md`. No file that reaches a binary differs. The tag is on
`88df7da` deliberately, so the release points at the commit whose record describes it.

### NOT VERIFIED

- **Setup has never replaced a RUNNING TradeAgent.** The relaunch test installed into an empty folder
  with nothing holding the files. The real update closes the app first and leans on
  `CloseApplications=yes` for the moment in between — that moment has been observed zero times.
- **The button has never been pressed end to end.** Download, checksum, launch, shutdown, relaunch as
  one continuous act is untested; each half is tested separately, which is not the same thing.
- **The download and the checksum have never run against a real asset.** Both are exercised through
  an injected seam in the tests. `Downloader.DownloadAsync` itself is the same code the AI runtime
  install already uses, which is proven on Windows, but this caller of it is not — and it stays that
  way until a release exists that is NEWER than the running build, because an app that is up to date
  never downloads anything. Cutting v0.1.1 is what exercises it.
- **`UpdateVersion` is compared against `Versions.App`,** which reads the entry assembly's version
  (`0.1.0` today, from `Directory.Build.props`). A release tagged with anything that is not a
  1-to-3-part number is refused rather than guessed, on purpose — including four-part tags, which is
  a real shape (`v0.101.2362.0`) and was met while choosing the stand-in feed.

### The release contract this depends on

The updater looks for exactly what `packaging/build.ps1` already produces, and a release that omits
either is not offered at all rather than half-installed:

- an asset matching `^TradeAgent-Setup.*\.exe$` — that is `TradeAgent-Setup-x64.exe`;
- optionally `SHA256SUMS.txt`, whose repository-relative paths (`artifacts/TradeAgent-Setup-x64.exe`)
  are matched on file name alone, because that is not what the asset is called.

**CI must not publish releases.** `build.yml` produces
`TradeAgent-windows-x64-NO-ATAS-ADAPTER` — a build that cannot trade through ATAS. Wiring that to a
release would push it to every user through the updater written above. A release is cut from a machine
with ATAS or it is not cut.

## Verified on real Windows 11 hardware, 2026-09-01, later session — the bridge could be frozen by the peer it was refusing

**Found by measurement, not by reading.** CI had been red on `windows-latest` for four commits with
four `BridgePipeAuthTests` failures. They were written on macOS, where the peer-identity half of the
bridge handshake does not execute at all, so they handed the bridge a credential recording no image —
which on Windows is a refusal at step 1, before the proof half those tests are about. That is correct
product behaviour ("could not check" is the state an impersonator would engineer) and the wrong test.

**Fixing the tests uncovered a real defect underneath them.** With the identity half satisfied, the
handshake completed for the first time on Windows — and the test host hung. No thread was in our code
at all; `dotnet-stack` showed thirteen threads, none of them ours, and no CPU. `dotnet-dump`'s
`dumpasync` named it exactly:

```
System.IO.StreamWriter+<<FlushAsyncInternal>g__Core|79_0>d
  System.IO.StreamWriter+<WriteAsyncInternal>d__71
    TradeAgent.AtasBridge.BridgeServer+<SendRaw>d__46
      TradeAgent.AtasBridge.BridgeServer+<Refuse>d__36
        TradeAgent.AtasBridge.BridgeServer+<Authenticate>d__35
          TradeAgent.AtasBridge.BridgeServer+<RunAsync>d__34
            TradeAgent.AtasBridge.BridgeServer+<DisposeAsync>d__47
              BridgePipeAuthTests+<A_pipe_owner_that_cannot_prove_the_secret_never_reaches_the_adapter>d__6
```

with the squatter pending in a write of its own. **Both ends were parked in a write that could never
complete, and the bridge was frozen inside the refusal of the very peer that froze it.**

**Why it was reachable, and it is not only an adversary.** `SendRaw` had no deadline — `AuthTimeout`
covered the read and nothing covered the write — and a Windows named pipe created with no buffer
completes a write only when the far end reads it, however small the frame. **TradeAgent's own
bridge-facing pipe was created with no buffer** (`AtasConnector.cs`, `0, 0` on both branches), so
every response and heartbeat the bridge sends was coupled to this process reading promptly, with no
slack. A stalled reader does what a hostile one does.

**The worst part is where it lands.** `BridgeServer.DisposeAsync` cancelled its token and then awaited
the loop — but a token cannot recall a write the kernel has already accepted, only closing the handle
can. `DisposeAsync` runs when ATAS unloads the strategy. Forever would have been ATAS's problem too.

### Fixed

- **`BridgeServer.WriteTimeout`** (default 10s), on every frame and on the queue behind it. A frame
  that has not landed ends the connection by disposing the pipe, which fails the pending write and
  returns the loop to reconnect. The abandoned task is observed rather than dropped.
- **`DisposeAsync` closes the pipe BEFORE it waits on the loop**, and the wait is bounded at 5s. It
  is also idempotent now.
- **`Push` no longer touches a disposed token source.** Nothing unsubscribes from the adapter, so
  ATAS can raise an event into a disposed bridge, and reading `_cts.Token` then threw straight back
  into ATAS's own event raise.
- **The bridge pipe is created with an 8 KiB buffer** on both branches, so a frame that fits no
  longer waits for a reader at all. The deadline is the backstop, not the only defence.
- **The four tests now record this process as the expected image**, which is what they meant, and the
  squatter's pipe is buffered like a normal peer's. The hostile zero-buffer choice is made explicitly
  by the one test that is about it.

### Verified, on the hardware

```
Passed!  - Failed:     0, Passed:    13, Skipped:     0, Total:    13, Duration: 1 s   (BridgePipeAuthTests)
Passed!  - Failed:     0, Passed:   146, Skipped:     0, Total:   146, Duration: 14 s  (IntegrationTests, whole project)
```

**And the new test was proven to bite.** With the pre-fix `BridgeServer` restored on the machine and
nothing else changed, the same test does not fail — it hangs, and the run has to be killed by a
watchdog:

```
Test Run Aborted.
The test running when the crash occurred:
TradeAgent.Tests.Integration.BridgePipeAuthTests.A_peer_that_accepts_the_connection_and_never_reads_cannot_freeze_the_bridge
```

The reproduction of the ORIGINAL four failures is recorded too, on the same machine, before any fix:
`Failed: 4, Passed: 7`.

### NOT VERIFIED, and one hazard left standing on purpose

- **This was never observed in production.** No frozen bridge has been seen inside ATAS; the defect
  was reached through a test double that stops reading. What is proven is that the path existed and
  that it no longer does.
- **`GatewayPipeServer` still creates its pipe with no buffer** (`0, 0`, both branches). That is the
  same class of coupling on the agent-facing pipe, and it is deliberately left alone: it was not the
  path measured here, and the agent IPC layer is not something to change on an inference the same
  hour as a deployment. Its own piece of work.
- **`Subscribe()` still has no unsubscribe.** The disposed check makes the event safe; it does not
  make the subscription go away.

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
- **The ATAS adapter.** Compiles against the real API, runs inside ATAS, and has placed, read back
  and cancelled orders on two different simulated backends. All four safety rules are implemented and
  three of them have been exercised on hardware.
- **Rule 1, proven.** The identifier survives ATAS being restarted — measured across a real process
  restart, not inferred. `SupportsClientOrderId` is true on evidence.
- **The bridge pipe authenticates in both directions**, with the residual against a same-user
  adversary written down rather than claimed away.
- **It updates itself.** The app asks GitHub for newer releases, offers them in a banner and on the
  Settings page, and installs only on two deliberate presses; the agent cannot reach any of it.
  v0.1.0 is published with the ATAS adapter in it, and the check has been watched reading it on
  Windows. What has never happened is an install — see the 2026-09-01 update section.
- **The AI inbox and the material ledger.** The owner can hand the agent programs, documents and data;
  every file that appears in the inbox or in the agent's tracked folders is recorded with a hash and a
  timestamp, and the agent records what it ran and what it derived from what. Measurement and claim are
  stored apart. Green and screenshotted on macOS; see the 2026-08-31 section for what that does not cover.

## What does not work yet

- ~~**`LIVE_CONFIRM` has never been walked.**~~ **Walked 2026-08-31, through ATAS, on the simulated
  crypto account.** Evidence in that section. What remains untested on that path is a *filling* order
  (today's rested and was cancelled), a decline, and the same path against a real broker.
- ~~**Platform and account cannot be changed after setup.**~~ **Fixed 2026-08-31: the Settings page.**
  Widening risk is two-press, narrowing it is one, and switching platform clears the chosen account
  because an account on one platform does not exist on the other. Seen rendering on Windows.
- **`LIVE_AUTONOMOUS` is refused, and correctly.** `ReconciliationProvable` is
  `SupportsClientOrderId && SupportsOrderHistory`. The first is now **true on evidence**; the second
  is **false for a known reason** — `IIndicatorDataProvider.GetService<T>()` throws
  `NotSupportedException` for every type, including one reachable as a property on the same
  interface, so no order-history route exists on this platform. One gate is open, one is shut on an
  answer rather than a gap. **Not to be "fixed" by hard-coding either true.**
- **Whether the identifier ever reaches the BROKER is unknown.** Rule 1 is proven across an ATAS
  restart, which is what reconciliation after a dropped connection needs. But ATAS rebuilding the
  order from the broker's answer and ATAS rehydrating it from its own local store are
  indistinguishable from inside a chart strategy. Only the broker's own report separates them.
- **The four obsolete order calls are still synchronous**, so the call deadline covers **one of five
  write paths** — a block in any of the other four stops the pipe loop while the heartbeat reports
  READY. **No longer gated on a measurement:** `OpenOrderAsync` was measured on 2026-09-01 and
  completes on ACKNOWLEDGEMENT. It is a decision now, and the reading makes it more delicate rather
  than less — flipping moves the failure mode from "WaitFor gives up and returns the true state" to
  "AtasCallTimeoutException, therefore UNKNOWN", and the margin protecting that was measured against
  a simulator with no broker behind it.
- ~~**TradeAgent's own UI has never been looked at on Windows.**~~ **Every page has now been seen
  there** (2026-09-01: Dashboard, Safety, Settings, Inbox, Checks, Activity, Chat, and the Updates
  card). It found four defects across two sessions, all fixed and re-photographed. **What has still
  never been seen on Windows is the reconciliation-override card** — correctly absent there, because
  nothing on that machine is flagged, so seeing it needs a flagged record in a scratch
  `TRADEAGENT_HOME` rather than a search that returns nothing and gets read as evidence.
- **Neither AI runtime is `Verified = true`.** That flag means proven on Windows.
- **The bridge pipe is not a boundary against a same-user adversary.** It authenticates in both
  directions now, and the AI runtime runs as the same OS user and can read the secret file. The
  peer-image rule is tamper-evidence, not a wall. Documented rather than claimed away.
- **`PriorSession` treats "a different session" as "a different process".** Two bridge strategies on
  two charts in one ATAS process are two sessions; closing that wants a process identity on the
  witness record. Documented in code.
- **The installer is unsigned.** Every user will see "Windows protected your PC". On a program that
  places trades, that wants a certificate.
- **The inbox's COPY path has never run**, though the inbox itself has: a file was handed over on
  Windows, recorded with a hash matching one computed independently beforehand, rendered, then removed
  and dropped from both tables. What remains unexercised is the drop handler, the picker's copy and
  the collision suffix — **and the picker cannot be driven from this harness**, so it needs a person
  at the keyboard. No agent has been asked to record its own work with `trade material` yet either.
- **Live money has never been touched.** Correct for this stage.
- **The updater has never updated anything.** v0.1.0 is published and the check reads it correctly on
  Windows ("you have the newest one"), the installer script compiles, and `/relaunch=1` provably
  starts the new build there — but nothing has ever been downloaded or installed by it, because
  nothing newer than the running build has been published. That needs a second release.

## Current blockers

1. **A code-signing certificate**, before this goes to anyone who did not build it.
2. **A real broker connection**, before any claim that an order reached one. Everything measured so
   far is against two simulated accounts — `CRYPTO5EB41` on Binance crypto-sim and `DEMO15M440CE` on
   ATAS Sim.
3. **Nothing else is blocked.** The machine is up, the bridge is current and authenticated, the
   capabilities are measured, `LIVE_CONFIRM` is proven through ATAS, and the remaining work runs on
   simulated accounts.

## Next integration target

Rewritten 2026-09-01. The first three of the previous four are done — `LIVE_CONFIRM` is walked,
`OpenOrderAsync` is measured, and every page has been seen on Windows.

1. **Watch one update install itself.** Publish a release newer than the running build and watch the
   whole act: banner, download, checksum, two-press install, Setup replacing a RUNNING TradeAgent,
   relaunch. It is the only untested half of a feature that is now live in front of a user, and an
   app that is up to date never downloads anything, so it cannot be tested without that release.
2. **Decide the four obsolete call sites.** The measurement is in; the decision wants a real broker,
   because the margin that makes it safe was measured against a simulator.
3. **The two remaining pieces of eyes:** the reconciliation card on Windows (needs a flagged record in
   a scratch home) and the inbox drop/picker copy path (needs a person at the keyboard).
4. **Give `GatewayPipeServer` a buffer and its writes a deadline** — the same coupling that froze the
   bridge, still standing on the agent-facing pipe.
5. Then the staged live trial: paper → extended paper run → one tiny live order → disconnect/recovery
   test → autonomous live permission. **Gated on a broker existing**, on top of every other gate.

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

## 2026-09-02 / 2026-09-03 — the hardening program, the update path proven, the setup journey walked

**Read `docs/hardening/HANDOFF-2026-09-03.md` first.** A program to reach a "trustable first deployment and monitoring
phase" was started on 2026-09-02 and run as units with an adversarial Claude verifier and a Codex cross-model review on
every one. The session's primary records (command logs, screenshots, mutation tables) were LOST when the Claude Code
process restarted on 2026-09-03 at 15:02 — the section below is reconstructed from the session transcript; every figure
quoted was quoted in an agent's report from a command that ran, and where the primary log is gone it says so. Full
per-unit detail: `docs/hardening/records/`.

### The update path, end to end on real Windows — VERIFIED (record: `docs/hardening/records/U3-update-proof.md`)

The published v0.1.0 was installed on the box from GitHub (sha256 `f243bdcc…0a1b` == manifest; Setup's own window only;
`/relaunch=1` started it, pid 7552). v0.1.1 was built on the box from `3931c10` (manifest: `version 0.1.1`,
`ATAS adapter PRESENT`, installer 112.2 MB; Windows test run 45/108/146 green), hashed identically on the box, the Mac,
`SHA256SUMS.txt`, the GitHub asset and the app's own download (`9b238179…a668`, 117,649,031 B), and published as
`v0.1.1` (target = full sha; a short sha is rejected). The running 0.1.0 offered it, was armed in two presses, Setup
replaced the RUNNING app (old pid last seen 23:13:32.168, Setup 23:13:32→41, new pid 9840 at 23:13:42), no console, no
UAC, no SmartScreen; **ATAS bridge READY 23:13:48.5** (~6 s after relaunch); `open_requests 1` unchanged (database
intact). **NOT VERIFIED:** signing/SmartScreen on a browser download; update while the AI runs; rollback or an
interrupted install; no Setup log for the app-driven install (no `/LOG`, no `SetupLogging`).

### One test read the real machine — FIXED (main `3931c10`)

The first v0.1.1 build on the box failed `AtasHealthTests.The_reporter_asks_the_platform_afresh_but_not_the_filesystem`
(expected FAILED, got READY: ATAS is installed and running there). The reporter's platform probe is now behind
`IAtasProbe`; production default byte-identical; the repro executed both ways on the Mac; 4 mutants bite; 299 green on
both machines.

### The setup journey, the override card and the refusal sentence — SEEN ON WINDOWS (record: `docs/hardening/records/U4-windows-eyes.md`)

In a scratch home with the installed 0.1.1: the journey walked end to end (8 of 16 screens shown, 8 self-verified;
no terminal, no admin, no credential typed); resume works; the override card reached through a genuinely ambiguous
order (bridge strategy stopped → approval → UNKNOWN + flagged + PAUSED → card → resolved with a note); the refusal
sentence rendered on the Dashboard and Checks rows; `probe atas` afterwards `orders=0 … client_order_id_attempts=0`;
the real home's database mtime unchanged throughout. **Sixteen UX defects** recorded for unit U6 — the first is that
the "Connecting to ATAS" setup screen never surfaces the connector's refusal detail. **NOT VERIFIED:** the eight
auto-passed screens (need a clean machine); the Inbox drop/picker COPY path (needs a person); the `PresentedNoProof`
sentence.

### Approval re-authorization — INTEGRATED (main `133c1bd..3f1d8f2`, record: `docs/hardening/records/U2b.md`)

`ApproveAsync` re-checks every gate at the moment a person approves (age first — 15-minute TTL, a judgment — then mode,
the authorization chain with the proposer's context so the kill switch refuses, platform identity, account, risk
limits), all under `_dispatchGate`; one clock governs the gateway and the request store. 30 new tests; verifier
PASS WITH LOW; 329 green; CI green on all three OS + package (run 33716906666).

### A confirmed hole, fixed on a branch not yet integrated (record: `docs/hardening/records/U2a.md`)

`TRADEAGENT_SESSION=operator trade buy …` made `AgentContext.IsOperator` true on the wire and skipped LIVE_CONFIRM parking
and the kill switch (proven over the pipe: a `session:"operator"` buy FILLED with STOP pressed). Fixed on
`u2a-pipe-hardening` (`AgentContext` is a sealed class; the pipe refuses the reserved word in seven spellings). **The
build on `main` and the published v0.1.1 still have the hole**; nobody is deployed. Integration order and the other
branches (`u2c1-dispatch-recovery`, `u2d-updater-fail-closed`, `u14-coid-witness-rewrite`) are in the handoff.

### Tests

Mac, `main` @ 3f1d8f2: `dotnet test TradeAgent.sln` → 75 / 108 / 146 = 329 passed, 0 failed; `dotnet build TradeAgent.sln`
0 warnings 0 errors. Branch tips: U2a 360, U2c-1 419, U2d 373, U14 377 (round 3; round 4 not re-run before the kill).
CI windows-latest on 3931c10 failed ONCE on `CoidWitnessTests.The_file_is_never_absent_while_it_is_being_rewritten`
("the temporary file was left behind") and passed on the next push — a load-dependent rename race that unit U14 turned
into a rule-1 durability fix.

## 2026-09-04 — U2a landed: the agent pipe, the connector deadlines, the replay contract and the emergency fast path

First unit landed under `docs/HOW-WE-BUILD.md`: twelve rounds of the old process on its branch, then the manager's
checklist alone. Merge `6138fdd`, 76 commits, 31 files, +9738/−309; rounds 1–12 in `docs/hardening/records/U2a.md`.

- **The operator-context hole is closed.** `AgentContext.IsOperator` is `private init`, only the static `Operator` sets
  it, the pipe refuses seven spellings of the reserved session string with INVALID_REQUEST. Before: `TRADEAGENT_SESSION=operator trade buy …`
  skipped LIVE_CONFIRM parking and the kill switch (Codex finding 4 on 283d942, proven over the pipe).
- **The agent pipe has a buffer, a progress-measured write deadline, and tracked handlers.** 8 KiB buffer; a stalled
  peer is dropped (`peer_stopped_reading`) while a slow reader making progress is not; disposal drains in-flight handlers
  (was: `DisposeAsync returned in 15 ms` mid-order) and logs `handlers_did_not_finish` with the request id whenever a
  DISPATCHING row survives disposal, unconditionally.
- **Replay contract.** The CLI prints `request-id:` before sending; "nothing sent" is distinguished from "reply lost —
  re-run with --request-id"; minted ids `op-{nonce}-{intent}-{index}` with `[A-Za-z0-9-]` enforced and a 61-character
  budget (64 − `TA-`) at the pipe; `docs/CONTRACTS.md` states it. `transport` is always emitted, `null` when unknown.
- **Connector deadlines and outcome words.** Every mutating operation has one absolute deadline; `Sent` / `PeerStalled`
  / `Busy` tri-state; a mutating step dispatched with no transport result reads `sent-not-confirmed` by the pipe server's
  own knowledge, a leg that never dispatched stays `not-sent`; five per-leg words (confirmed / rejected / not-sent /
  sent-not-confirmed / sent-still-working). Liveness = an answer within the ordinary deadline, 10 s grace.
- **Risk-reducing fast path keyed on intent.** `Cancel`/`CancelAll`/`Close` whoever asks; `EmergencyDeadline` 2 s, then
  "NOT confirmed — check your positions and orders in ATAS" plus the connection detail; a busy-but-progressing bridge is
  kept, a stalled one dropped. Drain bound from the connector's worst path; worst-case shutdown with an order in flight
  ≈ 265 s at shipped values, owed to the UI as a sentence (`docs/hardening/briefs/U6-U9-backlog.md`).

**Verified by running on this Mac at `51bf230` (the merge sha's code tree; `git diff --stat` over `src tests tools
packaging` between them is empty):** Debug `build --no-incremental` → 0 warnings, 0 errors; Debug `test` → 75 + 108 +
314, 0 failed; test-name diff against `main` → 0 removed, 168 added; secret scan → clean; at `6138fdd` Release build →
0 warnings and `ConnectorSendDeadlineTests` 47/47 twice. **CI run 33898144843 at `6138fdd`: windows GREEN 497; ubuntu
and macos RED, twice (rerun, same sha)** — ubuntu `A_peer_reading_below_one_chunk_per_window_is_busy_and_not_dropped`
both times; macos `An_emergency_spends_one_budget_across_the_gate_and_the_write` both times plus
`Local_queueing_under_load_does_not_disconnect_a_healthy_bridge` once; `package` skipped. Fix-forward
(`docs/briefs/U2a-fix.md`), not reset: the target is green, the class had never run on a hosted runner, and a reset
would strand two running builders. `main` is red on those two runners until the fix lands.

**NOT VERIFIED:** the box at this tip (the last hash-verified box run of the branch is round 7's tree); round 12 by any
verifier or by Codex — the money path is reviewed once on `main` before v0.1.2; mutant B4 (the Windows no-buffer pipe
stall), run by nobody; ATAS's real client-order-id limit and the `op-…` shape (a 64/65-character probe at v0.1.2).

**Deferred with an owner:** agent `close-all` legs with no fast path, sweep replay repeating effects, the operator Close
All deadline, a cancelled handler settling, gateway-side attempt marking → U2c-1 (C1–C5); LOW batch → the U6-U9 backlog.

## 2026-09-04 — U2d landed: the updater refuses instead of degrading

Second unit under `docs/HOW-WE-BUILD.md`: three old-process rounds on the branch, then one fresh builder pass on
`docs/briefs/U2d.md` and the manager's checklist. Merge `37e2d15`, 16 commits, 11 files, +2791/−46; rounds 1–3 in
`docs/hardening/records/U2d.md`. Before it, every way of losing the checksum (BOM, TAB, renamed asset, truncated hash,
empty or absent manifest) silently installed an unverified 90 MB executable, the installer asset was chosen by position,
and the "no install while an order is unconfirmed" stop existed only on the banner button.

- **Fail closed on the trust chain.** A missing, unfetchable or non-matching manifest refuses before any download with a
  readable reason; `DownloadVerifiedAsync` refuses a null or blank hash; more than one installer-pattern asset is
  ambiguous and refused; the file is re-hashed immediately before `Launch`; duplicate conflicting manifest entries
  refuse; 64 KiB / 2000-line manifest cap, enforced at read time (`ReadLimitedAsync` reads at most 64 KiB + 1, declared
  Content-Length refused unopened, 30 s fetch timeout); invisible, bidi, astral, private-use and unassigned characters
  in asset names refuse (`IsPlainFileName`); a negative count fails closed.
- **The hard stop is in `InstallAsync`, not on a button.** Behind a fail-closed `Func<int>? UnconfirmedWork` (null or
  throw = unknown work = refuse), re-checked before `Launch`; `TradingGateway.InstallInProgress` is consulted first in
  `TryAuthorizeExecution` (`UPDATE_INSTALL_IN_PROGRESS`, operator not exempt), goes up after the manifest is resolved
  and stays up after a successful `Launch`; the wiring is a testable seam, now named `UpdateTradingInterlock`, run
  against a real gateway in tests.
- **Refusals are what the owner sees.** The banner renders refusals only and expires them; `Dismissed` clears on a
  changed reason; a throwing `Activity` sink on the refusal path no longer replaces the owner's reason (one wrapped
  `Record(text, level)` that every sink call goes through); no checks after `Launch`; caller cancellation propagates as
  cancellation; `ReadLimitedAsync(_, int.MaxValue)` no longer overflows.

**Verified by running (the builder, quoted from its report; then the manager's gate):** item 1 RED on 3 tests
(`database is locked` out of `InstallAsync`), GREEN 77/77, mutant with the catch removed → 4 red; item 2 five wire tests
through a per-request `HttpListener` (65537 declared refused with 1 byte sent; chunked 65537 refused; 65536 read whole;
stall cut at the leash; healthy manifest resolves), each of three reverts RED; item 3 category rows pinned, mutant with
the three categories dropped → 3 red, `OverflowException` and a swallowed cancellation both RED then GREEN. Builder's
final on `bd5e390`: Debug and Release 0 warnings, 75 + 197 + 314 = 586, 0 failed. Manager's gate at the rebased tip
`37e2d15` in Release: build → 0 warnings, 0 errors; suite → 75 + 197 + 314 = 586, 0 failed; test-name diff against `main` → 1 removed (below), 90 added; secret
scan → clean; CI run 33901364151 → RED on all three, 313/314 each: ubuntu and macos on the two U2a runner-speed deadline tests
(`docs/briefs/U2a-fix.md`, in flight); windows on `CoidWitnessTests.The_file_is_never_absent_while_it_is_being_rewritten`,
the load-dependent rewrite race recorded on `main` at 3931c10 that U14 exists to close (U14a in flight). No file in
this unit's diff touches either class (`git diff --name-only b861ac9..37e2d15`).

**NOT VERIFIED:** `UpdateSources.Install` and every UI surface are executed by no test; nothing on the box. **Removed
test, deliberate:** `A_release_without_a_checksum_file_still_installs_without_inventing_one` (round 1, `df9b068`) pinned
the checksumless install this unit now refuses. **Deferred with an owner:** item 10, the provider counting every
wire-touched record through U2c-1's store query → U2c-1; `UpdateService.cs:255-265` is unchanged until then.

## 2026-09-04 — U14a landed: the rule-1 write-ahead record survives its own rewrite, and `Unreadable` is a value

Third unit under `docs/HOW-WE-BUILD.md`: ten old-process rounds on the branch, one fresh builder pass on
`docs/briefs/U14a.md` (the rebase over U2a plus three items), the manager's checklist. Merge `2c6826d`, 90 commits,
26 files, +9816/−263; rounds 1–10 in `docs/hardening/records/U14.md`. Before it, a claim whose temp-file rename failed
under a concurrent reader lived in a file no reader opened, and the order went to ATAS anyway — the durable answer to
"did this product submit this identifier" was NO for an order handed over microseconds later.

- **Write-ahead, or nothing is sent.** `Submitting` returns `bool`; `Place` throws `AtasRejectedException` ("nothing was
  submitted") on false, above `lock (_gate)`. One owner per witness: the lock is mandatory, a CAS miss is a refusal, the
  multi-writer rebase is gone. **Bridge protocol is 3**; a v2 bridge is refused by the app with the reinstall sentence.
- **Recovery is lineage, not time.** Envelope `generation` + `predecessor` fingerprint (FNV-1a 64); no adoption without a
  committed anchor; a zero-record temp is never adopted; a candidate may never shrink the set; two rivals → neither.
- **The five directives of round 10.** One function reads the sidecar filesystem and a concurrent change (listing before
  and after, names, lengths, mtimes) makes the read `Unreadable`, never a mixed-time view; rotation is atomic renames
  over a snapshot; the adapter teardown is a locked three-state machine; a current connection always says something
  about itself (`BridgeRow` shows the newest of three stamps).
- **U14a: `Unreadable` reaches every consumer.** `DecidingLine` is line / none / unread and `Settled` writes nothing over
  an unreadable set, so no RESOLVED marker closes a gap it never saw; the snapshot carries path → lines captured at read
  time, the support package renders from it and writes `bridge-sidecar-UNREADABLE.txt` into the zip instead of silently
  omitting files, the probe no longer reopens paths; candidates are read inside the before/after window, and one that
  changes under the reader is refused rather than adopted.
- **Rebase over U2a.** Two conflicts in `AtasConnector.cs`, neither a contradiction: both units' fields kept verbatim, and
  a duplicate `Observe` helper (identical bodies, U2a's chunked write and U14's abandoned frame read) merged into one.

**Verified by running (the builder, quoted; then the manager's gate):** item 1 RED (`RESOLVED coid-witness` found over an
unreadable sidecar) → GREEN, mutant collapsing `Unread` to `None` → 1/33 red; item 2 RED (the zip held only three files)
→ GREEN, mutant dropping the refusal → 2/5 red; item 3 RED (`"BRK-TA-REAL"` adopted from a temp that moved) → GREEN,
mutant dropping the listing comparison → 1/183 red. Builder's final at `db0d2c1`, Release: 0 warnings, 75 + 112 + 505
= 692, 0 failed; test names vs `01fcd60` ∪ `main`: 0 removed, 7 added. Manager's gate at the rebased tip `2c6826d`, Release:
build → 0 warnings, 0 errors; suite → 75 + 201 + 505 = 781, 0 failed; test-name diff against `main` → 1 removed (a rename, `…_is_not_written_over` present), 196 added; scan → clean; CI run 33904745608 → ubuntu and macos red on the two U2a runner-speed tests (fixer in flight); windows red 503/505
on two witness tests whose SETUP throws on Windows (`AppendAllText` sharing violation in the harness writer;
`Directory.Move` refused with a handle inside) — not product assertions; fixer `docs/briefs/U14-win.md` after U14b.

**NOT VERIFIED:** anything on Windows at this tip — the cross-process lock was proven on APFS only, the box's last
hash-verified run of this branch is round 10's; `tools/probe`'s new rendering (built at 0 warnings, never run, no test
references that program); the solution with `-p:AtasBridgeBuild=true`. **For the milestone review:** U14a made
`TradeAgent.Diagnostics` reference `TradeAgent.AtasBridge` for the sidecar snapshot type, so the bridge DLL now sits in
the app's output (`InstallBridge` still copies from `<base>/bridge`, read at `AtasInstallation.cs:191`, not run).
**Deferred with an owner:** resumable rotation, the fifth crash point, one serialised `Stop()`, the stamped connecting
status, the sidecar byte count → U14b (`docs/briefs/U14b.md`); bridge DLL redeploy at protocol 3 → the v0.1.2 box session.

## 2026-09-04 — U2a-fix landed: the three deadline tests measure the runner instead of assuming it

A fix unit under `docs/HOW-WE-BUILD.md`: one fresh fixer on `docs/briefs/U2a-fix.md`, product code untouched, one test
file changed (+203/−50). Merge `7e60973`. The variance was per platform, not per run, and the fixer measured it: with
the shipped 8 KiB pipe buffer and 1 KiB writes, the worst gap between two completed writes while a peer drips
1 KiB/800 ms is 1.60 s on macOS and 5.61 s on Linux, against the 2 s an emergency watches; ubuntu's failure reproduced
in a Linux container and every rewrite was verified there and on this Mac.

- `A_peer_reading_below_one_chunk_per_window_is_busy_and_not_dropped`: the peer takes one 7 KiB gulp mid-window
  instead of dripping — under one 8 KiB chunk, so both premises and the verdict stand; pacing cannot work (at 4 KiB/s
  Linux's worst gap is still 1.77 s). Mutant `WriteChunkBytes = 8192` → RED ("Not found: busy").
- `An_emergency_spends_one_budget_across_the_gate_and_the_write`: nineteen paced 80 ms reads became one 1.2 s wait the
  test holds, plus two premise asserts on when the gate was released. Mutant (a fresh clock after the gate) → RED at 3.21 s.
- `Local_queueing_under_load_does_not_disconnect_a_healthy_bridge`: no safe deadline existed (this Mac drains 300 calls
  in 220 ms; Linux at half a core needs >50 ms per chunk); the fake bridge now costs 20 ms a quote, a 6 s
  machine-independent floor against a 2 s deadline. Mutant (drop on gate expiry) → RED.

**Verified by running (the fixer, quoted; then the manager's gate):** the class 3× in Release on this Mac 47/47 each;
3/3 Linux and 5/5 macOS on the first test, 8/8 at 0.5 core and 2 cores on the third; full suite Release at the fixer's
base 497, 0 failed. CI run 33905797433 on its draft PR (merged with today's `main`): ubuntu SUCCESS, macos SUCCESS,
windows 503/505 with only the two U14-win harness tests red, which fail identically on `main`. Manager's gate at the
rebased tip `38e886e`, Release: 0 warnings; 75 + 201 + 505 = 781, 0 failed; test names vs `main` 0 removed, 0 added;
scan clean; the landed tip `7e60973` differs from `38e886e` by the report commit only (`git diff --stat` over code empty).
CI run 33908096382 at `7e60973`: ubuntu SUCCESS, macos SUCCESS, windows FAILURE on exactly the two U14-win harness
tests — the deadline class is closed on every runner; the witness-harness class is the only red left on `main`.

**NOT VERIFIED:** nothing on the box. **NOT done:** no product change, no `Timing` trait, no workflow change.

## 2026-09-04 — U14b landed: rotation resumes, the fifth crash point is closed, one teardown, a stamped status

Fourth product unit under `docs/HOW-WE-BUILD.md`: one fresh builder on `docs/briefs/U14b.md`, five items, the manager's
checklist. Merge `64cdb73`, 7 commits, 8 files, +813/−20 (`CoidWitness.cs`, `AdapterTeardown.cs`,
`AtasConnector.cs`, `docs/CONTRACTS.md`, tests). These are the places where round 10's structure had leaked back into
the old shape; after this unit the five directives hold at every edge the verifier and Codex had named.

- **Rotation is resumable.** At every append and every rotation start, a `.new` with no current is completed
  (`.new → current`) before anything else; when it cannot be, the append is refused and the standing is degraded with
  the reason, never silently. Before: after `current → .1` succeeded, a refused `.new → current` left every retry
  starting from a missing current and no note ever landed until restart.
- **The fifth crash point is closed.** Act 1 writes the next current log under a unique name nothing else holds
  (`FileMode.CreateNew`), never `Create` over an existing `.new`; the carry is recomputed from the snapshot taken before
  act 1. Before: one transient IO error after the open emptied the only copy of the unresolved marker.
- **`Stop()` is one teardown.** A compare-and-set under the state lock; overlapping calls join the first; `Stopped` is
  published exactly once after the last step; `Started()` stays refused throughout.
- **A peer inside the auth grace has its own status.** "connecting — waiting for the add-on to authenticate" is stamped
  at accept, newer than any marker; grace expiry replaces it with silent/unauthenticated; the hello with the live row.
  Before: the previous connection's refusal stayed on the row until the grace expired.
- **The sidecar is bounded by the bytes it is written in**, not UTF-16 chars; the dead `AppendDurably` is gone and one
  comment states the flush policy; `docs/CONTRACTS.md` records the residual that any process able to write in
  `Paths.BridgeDir` can drop `SupportsClientOrderId` with one unreadable file (fail-closed, same trust domain).

**Verified by running (the builder, quoted; then the manager's gate):** item 1 RED (completion removed) 3 failed / 35 → GREEN 38/38, mutant
"a failed completion lets the append through" → 2/38 red; item 2 RED (act 1 back to `.new` + `FileMode.Create`) 2
failed → GREEN 189/189, mutant `CreateNew` → `Create` → 1/41 red; item 3 RED (bare `Running → Stopping`) 2 failed / 14
→ GREEN 16/16, mutant = round 10's surviving MR10-4d (the transition outside the lock) → 1/16 red, caught by the
300 ms-order test `A_teardown_does_not_begin_while_a_write_is_inside_the_lock`; item 4 RED (`Connecting` removed) 3
failed / 5 → GREEN 45/45, mutant (unstamped, a marker always wins) → 1/8 red; item 5 RED "114180 bytes in the
current log" against a 65536 cap → GREEN 190/190, mutant `+= text.Length` → 1/42 red.
Builder's baseline at `a8fa06e`, Release: 0 warnings, 75 + 201 + 505 = 781, 0 failed; final at `3267fbf` 795, 0 failed,
0 names removed, 14 added. Manager's gate at the rebased tip `64cdb73`, Release: build → 0 warnings, 0 errors; suite →
75 + 201 + 519 = 795, 0 failed; test names vs `main` → 0 removed, 14 added; scan clean; CI run 33911240280 at `64cdb73` (and 33911280513 at the docs commit): ubuntu SUCCESS, macos SUCCESS, windows FAILURE
517/519 on exactly the two U14-win harness tests — the only red left on `main`, fixer in flight.

**NOT VERIFIED:** the box — no credentials in this session although the adapter and the teardown both changed, so
the bridge compile and `tools/atas-gate` at this tip are owed to the v0.1.2 box session; everything above is macOS/APFS — the Windows runner's own witness reds are the harness class
in `docs/briefs/U14-win.md`, dispatched next. **Known gap, stated by the builder:** `Rotate` also
returns whether the append may proceed when its own resume fails, and no test reaches that branch — under the
one-writer rule the byte counter is already negative whenever a rotation may be half done; it is there so the
invariant is local, not claimed as covered. No out-of-process SIGKILL run this unit.

## 2026-09-05 — U14-win landed: the witness tests reach their preconditions the way Windows allows

A fix unit under `docs/HOW-WE-BUILD.md`: one fresh fixer on `docs/briefs/U14-win.md`, product code untouched, two test
files changed (+152/−19 with the brief). Merge `73a1c85`. Three harness fixes, no `Skip`:

- **The rotating writer.** The harness appended with `File.AppendAllText` (shares READ only) while readers read the same
  sidecar with `File.ReadAllLines` (also READ only): a sharing violation on Windows that APFS never arbitrates. It now
  opens `FileMode.Append` sharing `ReadWrite | Delete` and waits a holder out, as the product's own append does. Mutant
  (`Standing` always `Clean`) → RED, 0 vs 51.
- **The missing bridge directory.** Windows refuses `Directory.Move` on a tree holding an open handle, and the witness
  lease (`FileShare.None`, instance lifetime) is one; disposing first loses the point, because `Submitting` leases BEFORE
  it reads and a re-lease into a gone folder answers "another writer owns this witness". The directory is now a
  removable link — a junction on Windows (no privilege), a symlink elsewhere. Mutant (`DirectoryNotFoundException`
  swallowed as not-failed) → RED, "changed underneath".
- **The churn test, the varying one.** `Expected 301 Actual 300` was NOT a leftover temp: `Submit` threw `Submitting`'s
  answer away, so a rename refused for its whole budget — order refused, claim rolled back, the mechanism WORKING — read
  as a lost record. It now asserts the promise: the seed plus exactly the accepted claims, temps ≤ refusals. Two
  whole-budget refusals injected at `replace`: new form green, old form RED 301 vs 299. Mutant (a record dropped after
  `Save` returned true) → RED, 301 vs 300.

**Verified by running (the fixer, quoted; then the manager's gate):** local at `f136a1b`, Release: 0 warnings; each test
3× green; both witness classes 191/191; suite 795, 0 failed. CI run 33924375698 on draft PR #2 (merged with `main`):
ubuntu SUCCESS, macos SUCCESS, windows 518/519 with all three target tests PASSED in the trx. Manager's gate at the
rebased tip `73a1c85`, Release: build → 0 warnings, 0 errors; suite → 75 + 201 + 519 = 795, 0 failed; test names vs `main` → 0 removed, 0 added; scan clean;
CI run 33926161681 at `ae0ab3c` (this landing's docs commit): ubuntu SUCCESS, macos SUCCESS, **windows SUCCESS 519/519**
— the first fully green test matrix on `main` since U2a landed; the `package` job ran for the first time since then
and SUCCEEDED (Inno Setup present, installer built and uploaded), so `packaging/build.ps1`'s staged-build check
accepts U14a's Diagnostics → AtasBridge reference in the no-adapter CI build; the box build with the ATAS adapter
is still owed at v0.1.2. The Windows flake class is intermittent: this run did not draw it.

**NOT VERIFIED:** nothing on the box. **A new Windows-only class, not this unit's:** on each PR run a different unrelated
test failed on windows-latest — `GatewayPipeBackpressureTests.A_close_all_wave_that_disposal_lands_in_leaves_nothing_unsettled`
(0 vs 1) on one run, `AtasProtocolTests.Capabilities_and_accounts_come_from_the_bridge_handshake` on the one before —
teardown and timing shapes, not product assertions, NOT investigated here; fixer `docs/briefs/U-win-flakes.md`.
**For the milestone review, stated by the fixer:** `CoidWitness` reads its sidecar with `File.ReadAllLines`, which shares
READ only, so on Windows a concurrent reader can push the product's own append into its retry budget — no red reachable
without a Windows machine, unchanged, NOT VERIFIED.

## 2026-09-05 — U2c1a landed: dispatch recovery on `main`, and the reconciliation rule derived correctly

Fifth product unit under `docs/HOW-WE-BUILD.md`: three old-process rounds on the branch (startup sweep, exhaustive state
mapping, catch-all after the wire, emergency-control records, target-based reconciliation — `docs/hardening/records/U2c1.md`),
then one brief and two builders (the first killed by a usage limit mid-item 3, the second continued from the branch).
Merge `19fe6e5`, 33 commits, 14 files, +4374/−100. Before it, a crash after the write-ahead was never swept and trading resumed
over a possibly-live order; unmapped connector states became ACKNOWLEDGED; Close All sent market closes with no record.

The rule, in `docs/CONTRACTS.md`: *a request leaves the unconfirmed set only on positive, definite, stable evidence about
its own target; anything else is inconclusive and keeps trading paused.* This unit makes the code obey it:

- **Only the record's own connector is evidence.** A record placed on A while B is connected is inconclusive, reason
  "placed on A; connected to B"; an empty book on B settles nothing.
- **Non-definite is never clear.** The live set is asked for `undecided` orders first, captured or not.
- **`Adopt` never treats the broker's UNKNOWN as resolved** — the flag every gate reads is no longer cleared on the
  strength of the broker not knowing.
- **"Held still" is not a verdict.** `_settleWatch`, `HeldStill` and `SignatureOf` are deleted; a working target stays
  inconclusive until a definite state: target terminal, absence past grace, definite CANCELLED, or the owner's card.
- **The latch covers the definite settle path.** A persist failure after the wire latches, files `settle_failed` off-thread
  and throws `STATE_DATABASE_CORRUPT`; `Settle` now runs before the activity line in `CancelAsync`/`ModifyAsync`, where a
  failing log write used to stop the settle from ever being reached.
- **Modify is judged against its target.** `CheckModification` refuses an answer whose order id, symbol or account is not
  the target's; `PriceCarries` accepts exactly floor/ceil of the request on the tick grid and refuses the price the order
  already had; `OrderInfo.Quantity` is defined in `Contracts.cs` as the total, never the remainder.
- **Rebase over U2a:** `GatewayTypes.cs` keeps U2a's sealed `AgentContext`; two post-rebase reds (U2b's disposal
  sentinel) were real interactions, fixed as item 0.

**Verified by running (the builders, quoted; then the manager's gate):** item 1 mutant `if (false && …)` → RED; item 2
mutant `undecided.Count > 99` → 3 RED; item 3 RED "the flag every gate reads was cleared…" → GREEN, mutant → RED; item 4
2 RED → GREEN, mutant `IsTerminal → IsLive` → 6 RED; item 5 2 RED (store put in `PRAGMA query_only`) → GREEN, mutant
`LatchUnconfirmed → ClearLatch` → 2 RED; item 6 6 RED → GREEN, mutant (prior-price clause `return true`) → 2 RED.
Builder's final at `bba9849`, Release: 0 warnings across 17 projects; Fault 191 / Unit 201 / Integration 506 = 898, 0
failed; names vs its baseline 19 added, 1 removed (a rename whose old name never existed on `main`). Manager's gate at
the rebased tip `19fe6e5`, Release: build → 0 warnings, 0 errors; suite → 201 + 191 + 520 = 912, 0 failed; test names vs `main` → 0 removed, 117 added; scan
clean; CI run 33927117880 at `b5446b7`: ubuntu SUCCESS, macos SUCCESS, windows FAILURE 519/520 on
`GatewayPipeBackpressureTests.Disposal_waits_for_a_cancelled_handler_to_record_what_it_knows` — `Assert.Null() Failure: Value is not null` — a
third distinct test in the Windows-only pipe-disposal class that `docs/briefs/U-win-flakes.md` is on; product or
harness NOT yet established (this unit's item 0 changed how a cancelled handler settles).

**NOT VERIFIED:** nothing on the box. **Gaps stated by the builder:** Close All's per-position `Settle` loop can still be
abandoned mid-sweep by a store failure (pre-existing, U2c1b's area); a persist failure inside the reconciler's `Resolve`
is caught per request and counted inconclusive — read, not tested. **Deferred with an owner:** the emergency-press
rewrite with C2/C3 → U2c1b; C1 (intent through the connector), C4 (cancelled handler settles), C5 (attempt marking) → U2c1c.

## 2026-09-05 — U-win-flakes landed: two Windows-only test failures were harness margins, and the matrix is green twice

A fix unit under `docs/HOW-WE-BUILD.md`: one fresh fixer on `docs/briefs/U-win-flakes.md`, product code untouched.
Merge `ff3ab4e`, 3 files, +64/−3 with the brief (`GatewayPipeBackpressureTests.cs`, `Harness.cs`).

- **`AtasProtocolTests.Capabilities_and_accounts_come_from_the_bridge_handshake`.** Measured, not inferred: a
  `StreamWriter` disposed with a write in flight throws `InvalidOperationException: The stream is currently in use by a
  previous operation` (reproduced against a pipe whose far end never reads), and the stub bridge's `Quietly` caught only
  `IOException` and `ObjectDisposedException`. `StubBridge.DisposeAsync` now waits, bounded at 5 s, for its loop to release
  the writer, and `Quietly` covers that exception and `TimeoutException`. 3× Release; class 7/7.
- **`GatewayPipeBackpressureTests.A_close_all_wave_that_disposal_lands_in_leaves_nothing_unsettled`.** Not the product:
  the gateway did what it documents — the drain expired, step 4 cancelled, step 6 logged `handlers_did_not_finish`. The
  fixture's margin was wrong: `close-all`'s prefix is five 500 ms calls and its first leg reached the broker at 3028 ms
  against a 3200 ms emergency budget, 172 ms of slack; the drain derived from that budget was 6300 ms against a 4557 ms
  wave. The fixture's budget is now 12 s, which a healthy run never waits out. Mutant: a 2 s stall injected once the wave
  is issued fails at 3200 ms with the CI assertion exactly (`Expected: 0 / Actual: 1`, sentinel `unsettled:1`) and passes
  at 12 s. 3× Release; class 32/32.

**Verified by running (the fixer, quoted; then the manager's gate):** Release at the rebased tip `bcaf4cf`: 0 warnings;
201 + 191 + 520 = 912, 0 failed. CI run 33929007448 on draft PR #3: ubuntu SUCCESS, macos SUCCESS, windows SUCCESS
(912, 0 failed), and the windows job re-run on the same sha SUCCESS with the same counts — two consecutive green Windows
runs, the acceptance. Manager's gate at `ff3ab4e`, Release: build → 0 warnings, 0 errors; suite → 201 + 191 + 520 = 912, 0 failed; test names vs `main`
→ 0 removed, 0 added; scan clean; CI run 33931934317 at `36cda57`: ubuntu SUCCESS, macos SUCCESS, windows FAILURE 519/520 on a FOURTH distinct timing
test, `ConnectorSendDeadlineTests.A_caller_that_cancels_an_emergency_releases_its_slot_and_still_counts_a_late_answer`
("the connection was judged on a cancellation that came from this side") — the two fixed here stayed fixed; the
class is open and gets a class-level brief, `docs/briefs/U-win-timing.md`, instead of a fourth instance fixer.

**NOT VERIFIED:** nothing on the box. **Open, stated by the fixer:** the third Windows flake,
`GatewayPipeBackpressureTests.Disposal_waits_for_a_cancelled_handler_to_record_what_it_knows` (red once, at `b5446b7`),
is untouched: measured over five runs its handler settles in ~30 ms against a 300 ms margin, not the 6 % shape of the
other two; it passed both acceptance runs; U2c1a's item 0 as its cause is NOT INVESTIGATED. If it reds again, it gets
its own brief with that measurement. No deadline shortened, no `Skip`, no premise assertion removed.

## 2026-09-05 — U2c1b landed: the emergency controls are one-shot + pause + human, and a replayed sweep sends nothing

Sixth product unit under `docs/HOW-WE-BUILD.md`: one fresh builder on `docs/briefs/U2c1b.md`, five items, the manager's
checklist. Merge `73f3542`, 4 commits, 15 files, +1569/−896. Before it, the emergency-press subsystem had outgrown its evidence
(Codex round 3, F9–F14): cancel-all captured ids but swept the account; close-all captured a quantity but closed whatever
existed; a definitely failed close held the press forever; a restart dropped a terminal press with a non-flat position.

- **A press is its records.** Cancel-all / close-all write per-target write-ahead records, send the wire calls, and from
  that moment trading is paused: the records count as unconfirmed work until the owner resolves them through the card.
  Nothing in memory survives a restart; the durable records ARE the press.
- **A second press while unresolved is refused** ("close-all sent at HH:MM; resolve it first"); there is no same-nonce
  retry and no press that a failed close can hold forever.
- **Cancel-all is per-order cancels of the captured set**; the account-wide sweep is gone from the gateway. Close-all
  re-reads the position immediately before the wire call and refuses (fresh two-press) if it drifted from the captured
  one. Completion and outcome read the account stored on the records.
- **Replay sends nothing.** Schema v3 adds `composite_request`: outer request id → captured child plan → per-child
  results, persisted BEFORE effects for the agent's sweep and the operator's press alike; a replayed outer id returns the
  stored outcome — order B created after the lost reply stays WORKING, position B stays open.
- **The operator's own press gets the fast path.** `RiskReducingScope` opens at the gateway inside the operator emergency
  methods, so button, CLI and agent inherit the 2 s emergency bound and the sentence; the pre-close position read too.
- **Deleted:** `OperatorPress`, `OutstandingPressNonce`, the `pressNonce` parameters, `NewOperatorPressNonce`, both
  press-replay `IdempotencyEnabled` checks, the gateway's `CancelAllOrdersAsync` call and the reconciler's CANCEL_ALL
  captured-set arm, three sweep hooks on the test connector. `ITradingConnector.CancelAllOrdersAsync` stays (17 ATAS
  send-deadline tests measure the bridge through it; nothing calls it; CONTRACTS.md says so) — U2c1c's to decide.

**Verified by running (the builder, quoted; then the manager's gate):** items 1–3 are one rewrite of two methods: RED
`op-close-…-ES is WORKING and unflagged` (3) → GREEN → mutant (drop `MarkNeedsReconciliation` from the write-ahead) → 3
red; RED `No exception was thrown` → GREEN → mutant (refusal returns early) → 2 red; mutant (ignore the drift + restore
the sweep) → 2 red (`Expected: 0 Actual: 1` closes, `0 vs 2` sweeps). Item 4 RED (order B swept by the replay; position
B closed) → GREEN → mutant (ignore the stored answer) → 2 red. Item 5 RED "the press took 6.0s against a 2s emergency
budget" → GREEN → mutant (`Begin()`, no budget) → 3 red. Builder's gate at `4e18a5f`, Release: 0 warnings; Unit 201 /
Fault 188 / Integration 524 = 913, 0 failed; names −20 / +21, all 20 pinning a retired path (3 retry, 3 `OperatorPress`,
5 press-set-and-restart, 4 press reconciliation, 4 F2-through-a-press, 1 partial-sweep answer), the F2 rule and "a
press is judged by its own records" re-homed onto surviving paths. Manager's gate at `73f3542`, Release: build →
0 warnings, 0 errors; suite → 201 + 188 + 524 = 913, 0 failed; names vs `main` → 20 removed (the six categories above, checked name by name), 21 added; scan clean; CI run 33932534657 at `44fb21f`: ubuntu, macos and windows all SUCCESS (913), `package` SUCCESS.

**NOT VERIFIED:** the Dashboard card (compiles; no UI run — see it on the Mac loop or the box); nothing on the box or
against real ATAS. **Outside its brief, declared:** ~12 lines in `GatewayPipeServer.CancelAll`/`CloseAll`, without
which item 4 was unreachable. **Deferred with an owner:** C1, C4, C5 → U2c1c (`docs/briefs/U2c1c.md`).
