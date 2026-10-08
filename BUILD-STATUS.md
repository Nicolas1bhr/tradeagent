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

## 2026-09-05 — U8 landed: the deployment and monitoring documents exist, and the user guide tells the truth

A docs-only unit under `docs/HOW-WE-BUILD.md`: one fresh builder on `docs/briefs/U8.md`, no code, no tests. Merge
`4f7baa7`, 7 commits. `docs/USER-GUIDE.md` 288 → 494 lines; `docs/DEPLOYMENT.md` 319 lines new; `docs/MONITORING-PHASE.md`
247 lines new. 86 behavioural sentences sourced to a file:line or a record section (the map is in the unit's report on
the branch); 11 marked "not yet walked" or NOT verified: SmartScreen on a browser download, a clean-machine install, the
eight auto-passing setup screens, `proto=3` on hardware, rollback, downgrade and an interrupted install, a support zip
opened or a log read on a deployed machine, the five-minute stop threshold (a judgment, no constant), `PresentedNoProof`,
real money, the strip's refusal.

- **Three guide claims were false and are corrected:** "Trading through ATAS does not work yet" (walked 2026-08-31);
  "12 screens" (16, `Onboarding.cs:4-12`); "There is no timer that decides for you" (a 15-minute approval TTL,
  `GatewayTypes.cs:27`). Six pages → seven; "add-on" → "bridge" except in two verbatim quotes where the app itself still
  says "add-on" (`OnboardingView.cs:941`, `BridgeProtocol.cs:167`) — an app inconsistency, recorded not papered over.
- **Could not confirm either way:** whether anything writes into `%LOCALAPPDATA%\TradeAgent\logs` — created
  (`Paths.cs:21`), swept by the support collector (`Doctor.cs:279`), and no writer found anywhere in `src/`.
- **Found while sourcing, a product defect:** there is no in-app way to reinstall the bridge once setup completes —
  setup renders only while onboarding is incomplete (`MainWindow.cs:183`), `Onboarding.Clear`'s only caller is the
  wizard's Back (`OnboardingView.cs:349`), and Checks prints repair text with no buttons (`DashboardView.cs:910-919`).
  So `Doctor.cs`'s "Press Install bridge." names a control that does not exist, and the protocol-3 refusal sentence
  ("reinstall the add-on from TradeAgent") has no in-product repair path. Written into both new documents as a defect,
  not a procedure → unit `docs/briefs/U-bridge-reinstall.md`.

**Verified by running:** nothing to build; the diff is `docs/` only (checked); scan clean. **NOT VERIFIED:** every
sentence the report marks "not yet walked"; no app run, no screenshot, no box.

## 2026-09-05 — U2c1c landed: intent survives the connector layer, a cancelled handler settles, the gateway marks every attempt

Seventh product unit under `docs/HOW-WE-BUILD.md`, the last of the three cut from the old U2c-1 round 4: one fresh
builder on `docs/briefs/U2c1c.md`, the manager's checklist. Merge `8591de8`, 5 commits, 13 files, +858/−121. With it, every
class the U2a reviews routed to U2c-1 (C1–C5) is closed on `main`.

- **Intent through the connector (C1).** `OrderIntent` on `PlaceOrderCommand` / `PlaceIntent`; both connectors honour
  it (`Rpc(…, reducesRisk)`; the Fake puts a closing placement on the operation deadline). Before: `trade close ES` over
  the real pipe against a bridge that answered the position lookup and then went mute took 10.05 s against a 2 s
  emergency budget; now under 6 s with "NOT confirmed". The other direction holds: an opening placement and every
  ordinary op behind a stalled write still wait their full term. A close takes what is LEFT of the operation deadline,
  not a fresh one. Drain rows keep their ordinary term on purpose: the intent is an obligation a third-party connector
  may ignore, and `docs/CONTRACTS.md` says so.
- **A cancelled handler settles (C4).** U2c1a's catch-all settled UNKNOWN + flagged only if disposal waited; at
  `SettleAfterCancelTimeout = 0` the row was still DISPATCHING when disposal returned. The post-cancel wait is now floored
  (`WriteBackAfterCancel = max(setting, HandlerOverhead)`); the drain still reads the raw setting.
- **A proven not-sent no longer pauses.** `ConnectorTransportException` + `NothingWritten` → CANCELLED, unflagged;
  `PossiblyWritten`, `ReplyReceived` and silence stay UNKNOWN. `cancel-all`'s `cancelled` / `not_cancelled` read the
  per-leg WORD, so a never-sent leg is never counted as a cancellation that landed.
- **The gateway marks the attempt (C5).** `TransportLedger.MarkDispatch()` at all five mutating dispatch sites, reusing a
  leg's record and attaching one where there is none; a ledger-blind connector that really performed a cancel now yields
  UNKNOWN + reconciliation at the record, never a clean `not-sent`; the three never-dispatched legs still read `not-sent`.
  The operator press paths mark their attempts but deliberately do NOT get the not-sent settle: a press record is written
  flagged before the wire and only the owner's card clears it.
- **Decision recorded:** `ITradingConnector.CancelAllOrdersAsync` stays — the only harness for the bridge's 17
  send-deadline measurements, which are about transport, not sweeping (`docs/CONTRACTS.md`).

**Verified by running (the builder, quoted; then the manager's gate):** item 1 RED "took 10.05s against a 2s emergency
budget" and "(intent: Close) waited 10.00s" → GREEN, mutant `opensExposure = OpensExposure(op)` → RED `Not found:
"not sent"`; item 2a RED `Expected: UNKNOWN / Actual: DISPATCHING` → GREEN; item 2b RED "trading is still paused after
a leg the connector PROVED it never sent" → GREEN, mutant (`is ReplyReceived`) → 3 RED; item 3 RED `Expected
"PossiblyWritten" / Actual null` → GREEN, mutant (drop `existing.Attempt()`) → RED. Builder's gate at `b53b29a`,
Release: 0 warnings, 17 projects; Unit 201 + Fault 188 + Integration 530 = 919, 0 failed; names 1 removed (a rename:
`…even_though_its_record_is_unknown` → `…and_leaves_nothing_to_reconcile`), 7 added. Manager's gate at `8591de8`, Release:
build → 0 warnings, 0 errors; suite → 201 + 188 + 530 = 919, 0 failed; names vs `main` → 1 removed (the rename above), 7 added; scan clean; CI run 33939362147 at `9d0d94b`: ubuntu, macos and windows all SUCCESS (919), `package` SUCCESS.

**NOT VERIFIED:** nothing on Windows, nothing against real ATAS, no UI run (Dashboard build-verified only).

## 2026-09-05 — the milestone review of the money path at `8591de8`: what an executed adversary found

The "ask questions later" step of `docs/HOW-WE-BUILD.md`, run once, after twelve landings: one fresh Opus reviewer told
to break the money path, with every finding an executed probe on branch `review-probes` (`b952851`, 13 probes, suite
932 / 0 failed there), and Codex `gpt-5.6-sol` read-only on the same sha in its own worktree. Both in
`docs/REVIEW-2026-09-05.md`. **Reviewer: HIGH 4 · MED 4 · LOW 2 · UNVERIFIED 6. Codex: HIGH 6 · MED 2 · UNVERIFIED 11**
(read-only claims; each fixer turns its finding red first or refutes it).

**Executed and money-wrong today (fix units cut, in this order):**
- **The reconciler writes off an order still on the wire** (`U-stranded`): `DispatchStrandedAfter` 30 s against a 50 s
  `WorstCaseOrderPath`; at 1/1000 scale the record reads CANCELLED "never reached the broker", trading resumes, the order
  fills, and the real answer is discarded as `already_settled`.
- **Two concurrent Close All presses reverse the position** (`U-press-atomic`): long 2 → short 2, both "ok"; the guard
  is unsynchronised, not wrong. Also: the press mints a client order id out of a broker symbol (58 chars with `' []'`;
  65 for MES against a 64 ceiling).
- **The update interlock asks the raw flag** (`U-interlock`): a DISPATCHING order pauses trading and does not stop an
  install; Setup launched over it (P4, P5). This is U2d's deferred item 10, now landable.
- **Settings fail OPEN** (`U-settings-closed`): an unparseable row re-arms the AI, empties the allowlist (read as
  everything allowed) and resets every cap, silently.
- **The kill switch is read once, then four connector reads happen before the wire** (`U-gates`, with Codex's Modify
  bypass and unknown-mode findings): Stop pressed mid-place → FILLED.
- The pipe's status answers with the operator's authorization, the frame cap counts UTF-16 chars, an agent can read an
  operator press record (`U-pipe-hello`); the schema documents the "held still" rule U2c1a deleted, `close-all` answers
  by record where `cancel-all` answers by leg word (`U-pipe-words`).

**Decisions that need Nicolas (recorded in `docs/RESUME-HERE.md`):** the ATAS platform installer is downloaded with
no checksum and run elevated — the vendor publishes no hash, so this is a design choice, not a line; and Codex F1: the
AI runs unsandboxed as the owner, so same-user credentials and the in-process gateway are not security boundaries —
the U12 containment question, challenged UNSOUND on 2026-09-03, still open. **Box items for v0.1.2:** whether ATAS
round-trips the malformed press id; whether a real bridge spends 30–50 s in gate + frame.

**NOT VERIFIED by either reviewer:** the witness, teardown, adapter and health code (barely touched), most of the App,
the approval chain, the material ledger, `ForceResolve`, `BridgePipeAuth`; nothing on Windows or real ATAS. The next
milestone's review starts there.

## 2026-09-05 — U-stranded landed: the reconciler never writes off an order whose dispatcher is still alive

The first fix unit from the milestone review, its finding 1 (executed as P6b): `DispatchStrandedAfter` was a 30 s
constant whose comment claimed "the connector's 10 s RPC deadline plus 20 s of slack", while `AtasConnector`'s worst
order path is 50 s, so a placement legitimately in flight for 30–50 s was written off CANCELLED "never reached the
broker", trading resumed, the order filled, and the real answer was discarded as `already_settled`. Merge `69c2545`,
4 commits, 7 files, +767/−34.

- **The bound derives from the connector.** `TradingGateway.DispatchStrandedAfter = Connector.WorstCaseOperationPath +
  GatewayOptions.DispatchSettleSlack` — 50 + 20 = 70 s shipped; an explicit option may only LENGTHEN it, as
  `HandlerDrainTimeout` does; the 30 s constant is deleted. Absence is judged from the later of dispatch time and the
  bound. A record this process never dispatched (a legacy stranded row) now waits bound + grace before absence
  settles it — a behaviour change beyond the finding, pinned by a test with a movable clock.
- **A live dispatcher owns its row.** While a handler is inside the connector call the reconciler does not move its
  row (a dispatch lease), and a `Settle` that arrives after the reconciler moved the row WINS when it carries the
  broker's definite answer (`LateDefiniteSettle`); `already_settled` is no longer the fate of a real FILLED or REJECTED.
  A genuinely stranded row, its dispatcher gone, still reconciles at the bound.
- **The row says what the owner needs:** "still on the wire for 90s of a possible 50s", naming the wire, not the bound.

**Verified by running (the builder, quoted; then the manager's gate):** `StrandedBoundDerivationTests` (3) RED "at 40s
unconfirmed: 1 … at 75s: resolved=1 … CANCELLED" → GREEN, mutant `DerivedDispatchStrandedAfter => 30 s` → 3 red;
`LiveDispatcherOwnsItsRowTests` (3) RED = UNVERIFIED 4 executed ("owned-1: never reached the broker … trading resumed:
True … already_settled" while the dispatch answered FILLED) → GREEN, one mutant per guard (lease check `if (false)` →
1 red; `LateDefiniteSettle` deleted → 1 red); the wording test RED → GREEN, mutant (name the bound) → red at "possible
70s". P6b lifted verbatim, run, then deleted: it now fails at its own premise one second in, because a placement in
flight is not stranded; P6a cannot compile, the constant it asserted on is gone. Builder's gate at `68b2883`, Release: 0
warnings; Unit 201 + Fault 195 + Integration 530 = 926, 0 failed; names 7 added, 0 removed. Manager's gate at `69c2545`,
Release: build → 0 warnings, 0 errors; suite → 201 + 195 + 530 = 926, 0 failed; names vs `main` → 0 removed, 7 added; scan clean; CI run 33944625461 at `798ed4b`: ubuntu, macos and windows all SUCCESS (926), `package` SUCCESS.

**NOT VERIFIED:** that a real bridge really spends 30–50 s in gate + frame (the review said the same); nothing on the
box, no real ATAS, no UI. **Still open, by design of the cut:** `UpdateTradingInterlock` asks the raw flag and sees none
of this (finding 3 → `U-interlock`).

## 2026-09-05 — U-win-timing landed: the Windows timing class is measured, and CI retries it once, on Windows only

A fix unit under `docs/HOW-WE-BUILD.md` for the class behind the one-test-per-run Windows reds: two fixers (the first
killed by a usage limit after measuring; the second continued from its branch), product code untouched. Merge
`fd5c6d9`, 7 files of substance (`build.yml`, `tests/Directory.Build.props`, `tests/Shared/TestTime.cs`, the two timing classes, the probe, the brief); six committed `TestResults/*.trx` files were removed by the manager at landing and the directory is now ignored.

- **The factor, measured by a probe kept in the suite** (ratios against this Mac, from the PR's own runs): windows CPU
  1.06–1.22, timer 1.02–1.18, pipe 2.24–4.27, **file IO 3.79 / 39.52 / 4.89 / 5.69**; ubuntu under 1.6 throughout;
  macos under 1.6 except timer 3.26 / 4.58. File IO is what the failures are made of and it spread tenfold across four
  runs, so no single scale covers it — option (b) was chosen on that ground. `TA_TEST_TIME_SCALE` is not set in CI and
  the probe prints `in-effect=1.00` every run to prove it.
- **The mechanism.** `Trait("Category","Timing")` on `ConnectorSendDeadlineTests` and `GatewayPipeBackpressureTests`
  (the only two classes with Windows-only reds: 4 + 2 tests across three fix units). `build.yml` runs
  `Category!=Timing` with no retry on all three platforms, then `Category=Timing` re-run ONCE on windows-latest alone,
  the first failure printed, named in the job summary, annotated and uploaded as its own trx.
- **It caught a real one in flight.** The first fixer's 2 s settle margin on
  `Disposal_waits_for_a_cancelled_handler_to_record_what_it_knows` failed again on attempt 2 of run 33941113025 with the
  identical symptom and was rescued by the retry; it was replaced, not kept: the settle window runs at the shipped 5 s,
  the slow call grows to 12 s so the derived 6.1 s drain still expires inside it, the record wait is 90 s against a
  measured 36 s. Mutant (1 ms settle) → RED with that message. A second premise gap (`OurWriteIsOver` leaving its own
  request pending) is closed; mutant (200 ms between the two frames) → RED.

**Verified by running (the fixers, quoted; then the manager's gate):** local Release 0 warnings; `Category=Timing` 3×
→ 81/81; `Category!=Timing` → 833; `--list-tests` splits 914 as 833 + 81 exactly. **windows-latest on `0ee01ef`, run
33943343018, attempts 1–3: all green, 920, 0 failed, 0 skipped, no retry used** (CI on a PR builds the merge with
`main`, hence 920). Manager's gate at `4988682`, Release: build → 0 warnings, 0 errors; suite → 201 + 195 + 531 = 927, 0 failed; names vs `main` →
0 removed, 1 added (the probe); scan clean; CI run 33946448047 at `572d72c`, the first run of the two-step workflow: both steps SUCCESS on ubuntu, macos and windows (927), no retry drawn, `package` SUCCESS.

**NOT done:** no product code; nothing shipped was shortened (one un-shortened); no premise dropped; no `Skip`; the
scale option is not wired into CI (`TestTime` stays as the knob for a deliberately slowed local run); no box.

## 2026-09-05 — U-interlock landed: the updater asks the gateway's own question, on whichever gateway is live

The review's finding 3 (executed as P4/P5) and Codex F5, and U2d's item 10 deferred since 2026-09-03: the update
interlock's "unconfirmed work" provider counted only persisted `needs_reconciliation` flags, so an order on the wire
right now, a stranded one, or a latched persist failure all read 0 and Setup launched over them; and a connector switch
built a gateway the interlock never met. One fresh builder on `docs/briefs/U-interlock.md`. Merge `7ef6b1e`, 4
commits, 5 files of substance, +725/−17.

- **One query.** `TradingGateway.WireTouched()` — flagged OR DISPATCHING at any age OR UNKNOWN OR RECONCILING, plus the
  in-memory latch — is what the provider returns; a strict SUPERSET of `Unreconciled()` (the trading question), not a
  second count, with a test pinning that direction, because widening the trading gate itself would stop trading during
  every ordinary placement. `UpdateService`'s doc comment now says so.
- **It follows the live gateway.** `Attach(Func<TradingGateway?>, …)` re-reads the gateway at every question and wires
  `InstallInProgress` onto each gateway it first sees; `AppHost` passes `() => Gateway`, so `SwitchConnectorAsync` has
  nothing to forget; a null source answers −1 = refuse.
- **Both directions.** A quiet wire still installs; the refusal is `Refused` + `RefusedPendingWork` + Failed, names the
  count in the app's words ("an order's outcome is" / "2 orders' outcomes are") and is written once into the activity
  history that the strip and the Settings card render.

**Verified by running (the builder, quoted; then the manager's gate):** item 1 RED 4 of 6 ("record state while
installing: DISPATCHING / updater UnconfirmedWork(): 0 / InstallAsync returned: True / Setup launched: 1"; the stranded
and latched shapes likewise) → GREEN, mutant (SQL back to `needs_reconciliation=1`) → 4 red; item 2 RED 2 (attached as
`AppHost` attached it, then swapped: "B HasUnconfirmedWork(): True / updater UnconfirmedWork(): 0 / Setup launched 1")
→ GREEN, mutant (bind once) → 1 red; item 3 RED 1 → GREEN, mutant (count dropped from the sentence) → 3 red. P4/P5
lifted, run verbatim against the fix (they fail at their premise: "updater UnconfirmedWork(): 1 / InstallAsync
returned: False / Setup launched: 0"), then deleted. Builder's gate at `5f7c690`, Release: 0 warnings; Unit 211 + Fault
195 = 406, 0 failed; **Integration NOT run by the builder** — starved for 40 min by 14 orphaned CPU busy-loops the
runner-speed probe of U-win-timing had left on this Mac (killed by the manager before the landing gate). Names vs
`main`: 0 removed, 10 added. Manager's gate at `7ef6b1e`, Release: build → 0 warnings, 0 errors; suite → 211 + 195 + 531 = 937, 0 failed (after the busy-loops were killed); names →
0 removed, 10 added; scan clean; CI run 33952871991 at `88617a0`: windows SUCCESS, macos SUCCESS, ubuntu FAILURE 194/195 on
`Fault.OperatorPressIsAnEmergencyTests.Cancel_all_gives_up_on_a_stalled_platform_inside_the_emergency_budget` — `the press took 3.4s against a 2s emergency budget`
— a fault-suite timing test outside the Windows-only retry category; product or runner NOT yet established.

**NOT done, stated by the builder:** `MainWindow`'s pre-press cosmetic line still reads the narrower
`status.UnreconciledRequests`, so the strip can offer an update seconds before the press is refused (the refusal itself
is right); `Doctor` and `GatewayHost` still ask `HasUnconfirmedWork()`, the trading question, on purpose. Nothing on
Windows, no UI, no real ATAS.

## 2026-09-05 — U-gates landed: every gate is decided at dispatch, for every mutating verb, and an unknown mode fails closed

Codex's F2, F3 and F4 and the review's executed finding 6, one class: a request reached the connector without every
gate that applied to it. One fresh builder on `docs/briefs/U-gates.md` (killed by a usage limit during its final
suite, after all three items and its report were committed; the manager's gate supplied that suite). Merge `6f1ba77`,
5 commits, 6 files of substance, +1147/−51.

- **Modify goes through the gates (F2).** Before: over the pipe in LIVE_CONFIRM with `MaxOrderQuantity = 1`, a modify
  from quantity 1 to 1000 answered `ok=True`, ACKNOWLEDGED, one modify call on the wire. Now it is risk-checked on the
  RESULTING order and parked like a place: `RISK_LIMIT_EXCEEDED`, no record, zero calls; an in-limits LIVE_CONFIRM
  modify parks at zero calls and the press sends one; PAPER in limits still applies; `RISK_CHECK_UNAVAILABLE` when the
  book cannot show the target. `ApproveAsync` gained a MODIFY arm.
- **An unknown mode fails closed (F3).** Before: `"mode": 999` with a real account read `ModeAllowsExecution: True`,
  `ModeIsLive: False`, and a buy filled. Now `ModeIsRecognised` gates execution, health reads PAUSED every refresh,
  `SetMode` refuses an undefined value, the owner gets a line naming the value, the value is NOT rewritten.
- **Gates decided at dispatch (F4 / finding 6).** Before, with a barrier inside the risk check's position read: Stop
  → `ok — FILLED`; `ActivateLive(false)` → FILLED; an approval whose mode moved to PAPER mid-check → FILLED; four callers
  against a rate limit of one → four fills. Now zero sends in each case, and five callers against a budget of three send
  exactly three: STOP, live activation and mode are re-checked at the dispatch gate after every awaited read, and the
  rate limit is one atomic take.
- **One property outside the unit's files, stated:** `GatewayPipeServer.ModifyHandlerPath` grew from 4W to 6W because a
  modify now issues a placement's chain, so the drain table's arithmetic test moved 256 s → 306 s and `modify` is the
  longest row.

**Verified by running (the builder, quoted; then the manager's gate):** item 1 RED "`ok=True`, `ACKNOWLEDGED`, modify
calls on the wire: 1" → GREEN; a mutant (check `before.Quantity`) SURVIVED the first test, so the test was made to name
the gate that refused and the mutant then dies (`Expected: "RISK_LIMIT_EXCEEDED" Actual: "APPROVAL_REQUIRED"`). Item 2
RED → GREEN, mutant (`||` for `&&`) → red. Item 3 four REDs → GREEN; mutants: drop `AuthorizeOrThrow` from the re-check
→ both barriers red; budget checked only in the risk pass → both concurrency rows red; a third (take the place at
`Commit`) SURVIVED, honestly stated: the place path holds `_dispatchGate` across check and commit, so the lock is
load-bearing only for `modify`. Builder's gate at `0bee79b`, Release: 0 warnings; 201 + 207 + 534 = 942, 0 failed;
names +16, 0 removed. Manager's gate at `6f1ba77` (rebased clean over U-interlock), Release: build → 0 warnings, 0 errors; suite →
211 + 207 + 535 = 953, 0 failed; names vs `main` → 0 removed, 16 added; scan clean; CI run 33953398616 at `90e1df9`: ubuntu, macos and windows all SUCCESS (953), `package` SUCCESS — the ubuntu
press-budget red of the previous run did not repeat (one occurrence in seven main runs; `U-press-budget` stays queued).

**NOT done, stated by the builder:** no box, no real ATAS, no UI, no money. The re-check's mode arm is reachable only
on the approval path (a fresh place re-reads the mode when it builds its record, so it parks instead). Untouched: the
press and composite regions, the pipe protocol, the updater, the connectors.

## 2026-09-05 — U-pipe-hello landed: the pipe refuses what it cannot name, counts what it says it counts, answers for the caller

The review's findings 7 and 10, UNVERIFIED 6 and Codex F8, all CONFIRMED red-first by one fresh builder on
`docs/briefs/U-pipe-hello.md` (a first builder was killed before its first commit). Merge `c766ae4` — the first
attempt at `db638ab` was REFUSED ("Not possible to fast-forward": `main` had moved) and this section was written and
pushed before that was noticed, because the merge's exit status was masked by a pipe; the branch was rebased in a
throwaway worktree, its code tree proven identical to the gated `db638ab` over `src` and `tests`, and merged. 8 commits,
7 files of substance, +1136/−38.

- **Protocol before session.** A hello naming protocol 2 was accepted (`"compatible": false`) and with a bad token
  answered `IPC_UNAUTHENTICATED`. Now `INCOMPATIBLE_PROTOCOL` is decided before the token, one `protocol_rejected` line,
  no session, the next buy `IPC_UNAUTHENTICATED`, zero orders; the current version still trades. `v` is checked at
  `hello` only, not on later frames.
- **Enumerated fields fail closed.** `tif: "ImmediateOrCancle"` reached the connector as Day and `tif: "999"` as the
  integer 999 (`TryParse` takes any integer, so an undefined enum value reached the connector). Now `INVALID_REQUEST`
  names the field and its four names; the same integer hole was closed on `kind` (notes), and `all` (orders) was
  `Str("all") is "true"`, so `"yes"` meant working-only — now a real boolean. `side` and `type` are not frame fields:
  side is the op, type is the prices sent, and a frame naming one is refused rather than ignored.
- **The frame cap counts bytes.** An unauthenticated peer's 2,700,096-byte frame was read whole and answered against a
  "1 MiB" cap that counted UTF-16 chars; `ReadFrame`, the only frame parser on this pipe, now counts bytes into a
  per-connection buffer and drops the peer past the cap; 1,020,093 bytes of CJK are still served.
- **Status answers for the caller.** An agent with the kill switch down read `execution_available: true` with no reason
  while its own buy was refused `AI_TRADING_STOPPED`; `status` and `schema` are now computed with the caller's context,
  the operator's own status unchanged. Fixed at the pipe, so the Dashboard keeps `AgentContext.Operator`.
- **An agent reads only its own records.** `trade order op-close-<nonce>-ES` returned the operator's press row whole
  ("you pressed Close all positions at 10:15"); an `operator` row never resolves on the agent path and an `op-` id only
  for its own session, answered identically to an id nobody minted; the agent's own buy and sweep-leg ids still resolve.

**Verified by running (the builder, quoted; then the manager's gate):** every item RED with the quoted output above →
GREEN → one mutant red each (`!=` → `<`: 2 red; the old `TryParse` line: 4 red; `MaxFrameBytes * 3`: 2 red; back to
`AgentContext.Operator`: 2 red; `MayRead → true`: 2 red). Builder's gate at `535e8d8`, Release: 0 warnings; Unit 211 +
Fault 207 + Integration 569 = 987, 0 failed; names nothing removed, +20 methods / +34 cases. `docs/CONTRACTS.md`,
`AGENTS.md` (via `WorkspaceBuilder`), the `--tif` help and `GatewaySchema`'s argument text say the same words;
`INCOMPATIBLE_PROTOCOL` is in `Errors.cs`. Manager's gate at `db638ab`, Release: build → 0 warnings, 0 errors; suite →
Unit 211 + Fault 207 + Integration 569 = 987, 0 failed — the integration project in two runs, because
`tools/mac-run.sh`'s old pkill (fixed at `f7f1baa`) killed three attempts while another leg iterated the app: 558 from a
relocated build plus the 11 path-dependent tests (26 with their classes) from the normal output path; names vs `main` → 0 removed, 34 added; scan clean; CI run 33957047971 at `6bd009e`: ubuntu, macos and windows all SUCCESS (987), `package` SUCCESS.

**NOT done, stated:** no box, no ATAS, no money, no UI; `TradingGateway.cs` untouched; `order` still reads the
BROKER's book unrestricted; a non-`op-` id is deliberately not session-scoped, since a restart renames the session.
Three one-line edits to `GatewaySchema.cs`'s argument descriptions, nominally U-pipe-words' file, far from the string
that unit owns.

## 2026-09-05 — U-bridge-reinstall landed: the repair the protocol-3 refusal names now exists in the app

U8's finding, closed by one fresh builder on `docs/briefs/U-bridge-reinstall.md`. Before it, the protocol refusal told
the owner to "reinstall the add-on from TradeAgent" and `Doctor.cs` said "Press Install bridge.", and after setup
there was no such control: setup rendered only while onboarding was incomplete, and Checks printed repair text with no
buttons. Merge `b37e4de`, 5 commits, 14 files of substance, +533/−66.

- **The control.** Checks has a card, "The ATAS bridge", in the owner's words ("the small piece TradeAgent puts inside
  ATAS so the two can talk to each other… putting it back is the repair. Close ATAS first if it is open.") with a
  `Reinstall the bridge` button, two-press, that runs the same `InstallBridge` the setup step runs and reports success
  or the reason; `AtasHealthReporter.Forget` makes the bridge row re-derive its status; a copy ATAS refuses because it
  holds the DLL answers `ATAS_BRIDGE_IN_USE` — "close ATAS and press again", no other instruction. Also on Settings.
  Seen on the Mac loop: the card and the button on Checks and on Settings (screenshots in the session scratchpad;
  resting state only — the armed label and the result sentence were not photographed, the shell had no Accessibility
  permission to press).
- **The sentences agree.** The protocol refusal, the Checks repair text and `Doctor.cs` name the control by its
  on-screen label; the unexpected-failure sentence (`UNKNOWN_ERROR`) no longer names a "Diagnostics screen" that does
  not exist; "add-on" → "bridge" in the two places the brief named and three more live sentences in `AtasConnector`
  (`PendingHello`, `Silent`, `PresentedNoProof`); two catalogue repairs that named a Retry button no screen ever had
  (`ATAS_NOT_FOUND`; `AI_INSTALL_FAILED` → "Try again").
- **The docs.** `USER-GUIDE.md`'s "no button for that repair yet" paragraph became how to press it; `DEPLOYMENT.md`'s
  redeploy note and §5 point at the button, with its "not yet walked" bullet naming what is NOT verified;
  `MONITORING-PHASE.md`'s row-4 grep string was a sentence the unit had just deleted, and was corrected.

**Verified by running (the builder, quoted; then the manager's gate):** item 1 RED = 13 compile errors for the names
that did not exist → GREEN 6/6 (`BridgeReinstallTests`), mutant `Forget() { }` → RED; item 2 RED 2 (`Not found:
"Reinstall the bridge"`; `Found: "press Retry"`) → GREEN 218 unit, mutant (old refusal text) → RED. Builder's gate at
`e658947`, Release: 0 warnings; 218 + 207 + 569 = 994, 0 failed; names 7 added, 0 removed. Manager's gate at `5cb3301` (the merge sha's code tree, docs aside),
Release: build → 0 warnings, 0 errors; suite → 218 + 207 + 569 = 994, 0 failed; names vs `main` → 0 removed, 7 added; scan clean; CI run 33958409134 at `5392dd0`: ubuntu, macos and windows all SUCCESS (994), `package` SUCCESS.

**NOT VERIFIED:** the real ATAS-holds-the-DLL refusal (no box; the test stands a destination the copy cannot
overwrite); the armed label and the result sentence on screen. **NOT done:** three "press Retry" sentences in
`Prerequisites.cs`; two `Versioning.cs` comments quoting the retired refusal. **A trap the builder paid for:** Avalonia
aborts at startup with `RenderTimer … -6661` when the Mac's display is asleep, which reads like a broken build;
`caffeinate` first (now in `docs/RESUME-HERE.md`'s traps).

## 2026-09-05 — U-press-atomic landed: one emergency press at a time, a replay bound to its verb, ids the gateway may send

The review's findings 2 and 4 and UNVERIFIED 5 (executed as P10 and P2) and Codex's F6 and F7, by one fresh builder on
`docs/briefs/U-press-atomic.md`. Merge `a53378b`, 9 commits, 11 files, +930/−25.

- **Check and first row are one statement.** P10 reproduced under a barrier: two presses released together inside the
  capture read → "press A: ok, press B: ok, close calls on the wire: 2, position after: ES −2", two press rows. Now
  `ExecutionRequestStore.TryCreateFlagged` writes the flag AND `NOT EXISTS (a flagged row of this control)` in one
  INSERT, so the check and the first row hold across the two processes that reach the button: "press A:
  EMERGENCY_PRESS_UNRESOLVED — close-all sent at 09:56; resolve it first", one close call, position flat, one row; the
  other direction (resolve, press again) sends.
- **A replay is bound to its verb and session, and looked up before any live read.** Before: cancel-all with a
  close-all's id was ACCEPTED as a close-all; session B replaying session A's id was accepted; an offline replay threw
  the connector's disconnect. Now `ReplayOf` checks `op` and `agent_session_id` (existing columns, no schema change):
  "INVALID_REQUEST — request id 'cr-1' already names a 'close-all'", zero wire calls, zero leg records, zero position
  reads during the replay; proven over the real pipe too. `BeginCompositeAsync` takes the capture as a delegate and
  never runs it on a replay.
- **The press mints ids the gateway may send.** Before: `TA-op-close-…-ES 12-25 [CME Globex Futures]` (58 chars,
  `' []'` outside the charset), 65 chars for MES, and cancel-all carried a BROKER order id in its leg id. Now
  `PressLegId(kind, nonce, index)` for both controls (`TA-op-close-…-0`, 30 chars), the target stays on the record;
  `MaxClientOrderIdChars` / `MaxRequestIdChars` / `IsSendableId` on the gateway, `OpenPressRow` refuses an id that breaks
  them, and a reflection test pins the gateway's budget to the pipe server's private one (61 == 61). UNVERIFIED 5
  reproduced (a latch naming a row whose insert failed held trading paused with nothing to resolve) and closed: the
  latch follows the create.

**Verified by running (the builder, quoted; then the manager's gate):** item 1 RED → GREEN, mutant (the exclusion
clause neutralised) → red `Expected: 1 Actual: 2`; item 2 four REDs → GREEN, mutants (verb check compares the stored
op to itself → 3 red; lookup after the read → 1 red); item 3 RED → GREEN, mutants (target back in the leg id → 2 red;
latch before the create → 1 red). Builder's gate at `052a249`, Release: 0 warnings; Unit 211 + Fault 218 + Integration
536 = 965, 0 failed; names 0 removed, 11 added; `docs/CONTRACTS.md` states the atomic claim, the leg-id shape and the
binding; the `AGENTS.md` template states the binding. Manager's gate at `a53378b`, Release: build → 0 warnings, 0 errors; suite →
218 + 218 + 570 = 1006, 0 failed; names vs `main` → 0 removed, 12 added; scan clean; CI run 33958941039 at `9cc3fb4`: windows SUCCESS, ubuntu SUCCESS, macos FAILURE 217/218 on this unit's own
`PressAtomicityTests.Two_close_all_presses_released_together_send_one_close_and_refuse_the_other` — `Assert.Single() Failure: The collection did not contain any matching items` —
green 3× on this Mac and in the manager's gate; harness margin or real race NOT yet established (fixer briefed).

**A combination fix rode along (`U-press-pipe-fix`, one test file):** at the rebased tip U-pipe-hello's new
`An_operator_press_record_is_not_readable_over_the_agent_channel` failed ("Sequence contains no matching element") — its
lookup spelled out the id shape this unit changed; a fresh fixer made it read the leg id off the press record, kept the
assertion, and U-pipe-hello's mutant (`MayRead → true`) still goes RED at the new shape. `GatewayPipeServer.cs` untouched.
**NOT done, stated:** `GatewayPipeServer.CancelAll`/`CloseAll` still read the book before `BeginComposite`, so an
agent's OFFLINE replay fails on the read (the binding reaches them) → `U-pipe-words`. No box, no ATAS, no UI run.
**Side effect for `U-press-budget`:** the press row is one insert instead of insert + `MarkNeedsReconciliation`.

## 2026-09-05 — U-pipe-words landed: the schema says what the reconciler does, and close-all answers by the leg word

The review's findings 8 and 9 (executed as P8 and P11), by one fresh builder on `docs/briefs/U-pipe-words.md`; its third
item, an offline replay that never reads the book, was blocked until U-press-atomic's `BeginCompositeAsync` reached
`main` and goes to a fresh fixer (`U-pipe-replay`). Merge `4305ae6`, 5 commits, 4 files of substance, +457/−7.

- **The runtime schema stops promising deleted rules.** At `AbsenceGrace = 0` the reconciler drove a WORKING target to
  RECONCILING (inconclusive) twice while `cancel_and_modify_outcomes` promised REJECTED "when it has stayed working and
  unchanged for a whole grace window". The whole schema was swept against the reconciler: two false sentences, both in
  that field — the held-still verdict, and "a price within one tick of the request on the instrument's grid counts",
  which `PriceCarries` replaced with floor/ceil of the request — plus `unknown_state_meaning`, true but naming only the
  UNKNOWN half of a failed mutation (a proven-unsent one, CANCELLED and unflagged since U2c1c, had no entry). The rest
  checked, unchanged. One-tick is pinned by text only: no test connector can produce it.
- **`close-all` answers by the leg word.** A never-sent close leg answered `not_closed` with `"state":"CANCELLED"` and
  no `outcome` (a `KeyNotFoundException` on the field); now `closed` / `not_closed` read the per-leg word as
  `cancel-all`'s do, `outcome` is present, and `closed` means the word AND a FILLED record — `confirmed` reads off a
  CANCELLED or FILLED row, and a cancelled closing order flattened nothing. No `AGENTS.md` sweep paragraph exists to
  match, so the parity lives in the schema's two op descriptions.

**Verified by running (the builder, quoted; then the manager's gate):** item 1 RED `SchemaMatchesReconcilerTests` 3/4
(CANCELLED → CANCELLED, FILLED → REJECTED, WORKING → RECONCILING `resolved=0 inconclusive=1` against the promise) →
GREEN 4/4, mutant (the clause back in) → 2 RED; item 2 RED `CloseAllAnswersByTheWordTests` 3/4 → GREEN 4/4, mutant
(both halves back to `ExecutionRequest.State`, `outcome` dropped) → 2 RED. Builder's gate at `8ca805c`, Release: 0
warnings; 211 + 207 + 577 = 995, 0 failed; names 0 removed, 8 added. Manager's gate at `2e6b1f3` (the merge sha's code tree, docs aside), Release: build →
0 warnings, 0 errors; suite → 218 + 218 + 578 = 1014, 0 failed; names vs `main` → 0 removed, 8 added; scan clean; CI run 33959452818 at `bacbc29`: ubuntu, macos and windows all SUCCESS (1014), `package` SUCCESS — the macos
double-press red of the previous landing did not repeat (one occurrence; `U-press-atomic-mac` queued).

**NOT done, stated:** item 3 — over the pipe, after a completed sweep and `Faults.Disconnected`, the same id answered
`TRADING_CONNECTION_MISSING` with one connector call during the replay; the fix is one line at each of
`GatewayPipeServer.CancelAll` and `CloseAll` (`BeginComposite` → `BeginCompositeAsync`, the book read inside the
capture delegate) and was left for a fixer rather than bypass the verb/session binding with an early `Composites.Get`;
the RED test was written and not committed so the gate stayed green. No box, no ATAS, no UI.

## 2026-09-05 — U-pipe-replay landed: an agent's offline replay of a sweep never reads the book

The gap two units measured and neither could fix in its own files (U-press-atomic owned the gateway, U-pipe-words the
pipe server before the async entry point had landed), closed by one fresh fixer on `docs/briefs/U-pipe-replay.md`.
Merge `11248b6`, 5 commits, 4 files of substance, +272/−14.

- **One change at each call site.** `GatewayPipeServer.CancelAll` and `CloseAll` read the book before calling the
  synchronous `BeginComposite`, so a replay with the connector unreachable failed on the read. Both now `await
  BeginCompositeAsync` with the book or position read inside the capture delegate: a replayed sweep returns its first
  answer byte for byte and makes zero connector calls; a NEW id with the connector unreachable still answers
  `TRADING_CONNECTION_MISSING` and claims no `composite_request` row; a fresh sweep reads exactly once. The synchronous
  `BeginComposite` stays for the two operator press paths.
- **The contract says so.** `docs/CONTRACTS.md`'s replay paragraph states that a replayed sweep performs no read; the
  `AGENTS.md` template says it in one sentence, pinned by an assertion in the existing
  `The_agent_is_told_the_things_it_must_not_get_wrong`.

**Verified by running (the fixer, quoted; then the manager's gate):** RED over the real pipe, `OfflineSweepReplayTests`
2 of 4 — completed sweep, `Faults.Disconnected`, same id → `ok=False {"code":"TRADING_CONNECTION_MISSING"}` and
"connector calls during the replay: 1", both sweeps → GREEN `ok=True`, the first answer byte for byte, 0 calls; mutant
(the read hoisted back above the call at both sites) → the same 2 RED; second mutant (the sentence deleted) → RED
`Not found: "reads nothing from the platform"`. Fixer's gate, Release: 0 warnings, 17 projects; 218 + 218 + 582 = 1018,
0 failed pre-rebase; at the rebased tip 581/582 with one `Category=Timing` test thrown by its own setup while another
builder's suite ran on this Mac, the class then 3× → 34/34; names 0 removed, 4 added. Manager's gate at `11248b6`, Release:
build → 0 warnings, 0 errors; suite → 218 + 218 + 582 = 1018, 0 failed; names vs `main` → 0 removed, 4 added; scan clean; CI run 33966343677 at `a8fc709`: ubuntu SUCCESS, windows SUCCESS, macos FAILURE 217/218 on
`PressAtomicityTests.Two_close_all_presses_released_together_send_one_close_and_refuse_the_other` again — the second time
in three macos runs; the product is right in that test's own log, the assertion names the wrong guard (`U-press-atomic-mac`,
landing: its PR run 33967839971 was green on all three platforms). The docs commit `572c2be` after it drew a NEW
one-off windows red, run 33966990967: `SweepRequestIdTests.A_five_order_sweep_carries_a_mix_of_outcomes_in_one_answer`
— `Not found: "confirmed"` in `[sent-not-confirmed, rejected, not-sent, not-sent, not-sent]`; not in the `Timing`
category, did not recur on the next windows run; recorded, briefed if it recurs.

**NOT done, stated:** `TradingGateway.cs`, `GatewaySchema.cs` and the connectors untouched; no box, no ATAS, no UI.

## 2026-09-05 — U-press-atomic-mac landed: the double-press test asserts the invariants, not the schedule

A test-only fix under `docs/HOW-WE-BUILD.md` (one fresh fixer, after a first one was killed before starting): on
macos-latest, twice in four runs, U-press-atomic's own barrier test failed while the product was RIGHT — one close on the
wire, the position flat, one press row — because press A's fill landed before press B's re-read and B was refused by
the drift re-read instead of the atomic press guard, and the test had asserted the guard's sentence. Merge `3224183`,
5 commits, 2 test files, +327/−48 with the brief; no product file touched.

- **The race test asserts what the product promises:** exactly one close, two orders, flat, one press row, one nonce,
  and press B refused with nothing sent by ANY of the three truthful answers (the atomic claim, the drift re-read, or a
  capture read that found nothing open). RED was CI run 33958941039 byte for byte, reproduced with a seam that lands A's
  fill before B's re-read; mutant (the claim clause AND the drift guard neutralised) → RED `Expected: 1 Actual: 2`, two
  press rows.
- **The atomic guard keeps its own deterministic proof:** `Only_the_atomic_claim_can_refuse_a_press_whose_drift_re_read_saw_no_change`
  holds the winner's close until the other press has ANSWERED, so nothing else can refuse it — "EMERGENCY_PRESS_UNRESOLVED
  — close-all sent at HH:MM; resolve it first", the drift sentence absent; mutant (`NOT EXISTS` alone) → the same RED.
- **The macos schedule now runs everywhere:** item 1's RED seam is kept as a third test,
  `The_drift_re_read_refuses_the_second_press_when_the_first_fill_landed_first`. The harness's `RecordingConnector` gained
  a null-by-default `Seam` and a gated `Close`.

**Verified by running (the fixer, quoted; then the manager's gate):** class 4/4 seventeen times, twelve under load;
fixer's gate at `dab3b0e`, Release: 0 warnings; fault 220 3×; 218 + 220 + 582 = 1020, 0 failed; names 0 removed, 2
added; **CI run 33967839971 on draft PR #5 (merged with `main`): ubuntu, windows and macos all SUCCESS, 1020 each,
`package` SUCCESS.** Manager's gate at `d94fd12` (the merge sha's code tree, docs aside), Release: build → 0 warnings, 0 errors; suite → 218 + 220 + 582 = 1020, 0 failed; names vs `main` →
0 removed, 2 added; scan clean; CI run 33969815371 at `8e821fe`: ubuntu, macos and windows all SUCCESS (1020), `package` SUCCESS — macos green on the
rewritten test.

**NOT done:** no product file, no box, no ATAS, no UI.

## 2026-09-05 — U-settings-closed landed: an unreadable settings row disarms nothing and allows nothing

The review's finding 5 (executed as P1) and the first item of the old U2c-2, by two builders on
`docs/briefs/U-settings-closed.md` (the first killed by a usage limit inside item 1; the second judged its four
uncommitted files on their merits, kept them, and found a gap the brief had not named). Merge `bcffac0`, 7
commits, 29 files, +946/−28. Before it, `LoadSettings` caught every deserialization failure and returned fresh-install defaults: the
owner's `ai_trading_stopped: true` and `instrument_allowlist: ["MES"]` read as stopped = false, allowlist = [] which
`InstrumentAllowed` read as everything, and caps at their defaults, silently.

- **An unreadable row is the most restrictive row.** `Unreadable()` = OBSERVE, stopped, live off, no account, caps 0,
  allowlist []; the raw row is kept as `settings_unreadable`; health reads PAUSED from the constructor; `PlaceAsync` is
  denied, zero orders sent. A value that PARSES to an undefined mode is U-gates' `ModeIsRecognised` check, asserted as
  the boundary by its own test; this unit owns the row that does not parse.
- **The gap found on the way:** an EMPTY row (a write truncated to zero bytes) counted as a fresh install and handed out
  fresh-install permissions; only a genuinely ABSENT row is a fresh install now.
- **An empty allowlist allows nothing.** `InstrumentAllowed` no longer begins `Count == 0 ||`; a populated allowlist
  still allows exactly its members.
- **The owner recovers in the app.** The Safety page carries the caution card ("your settings could not be read;
  trading is stopped until you review them") and saving rewrites the row; `docs/USER-GUIDE.md` has the section. No
  path, no terminal, anywhere in the sentences.

**Verified by running (the builders, quoted; then the manager's gate):** item 2 RED (P1 lifted onto `main` PASSES,
which is the defect) → GREEN, mutant → 2 red; item 1 RED 6 of 8 over `main`'s `LoadSettings` (`CouldNotBeRead False`,
`AiTradingStopped False` on a row reading true, `Mode PAPER` invented from `LIVE_LOCKED`, caps 1/2/6) → GREEN, second
RED 3 (the empty row) → GREEN, mutant (`catch` → defaults) → 6 of 12 red; item 3 GREEN on the first run (the mechanism
is item 1's), mutant (`MarkSaved()` deleted) → 1 of 14 red; proven in the RUNNING app over its pipe: `trade status` →
"Execution capability PAUSED your settings could not be read; … on the Safety page", mode OBSERVE, stopped, allowlist
[], caps 0. Builders' gate at `bab42fc`, Release: 0 warnings; 219 + 236 + 570 = 1025, 0 failed; names vs the branch
base 16 added, 1 removed (a rename, `…everything_is_allowed` → `…nothing_is_allowed`). Manager's gate at `bcffac0`,
Release: build → 0 warnings, 0 errors; suite → 219 + 238 + 582 = 1039, 0 failed; names vs `main` → 1 removed (the rename), 20 added; scan clean; CI run 33970610692 at `c4ca0f1`: ubuntu, macos and windows all SUCCESS (1039), `package` SUCCESS.

**A combination fix rode along (`U-settings-replay-fix`, test fixtures only):** at the rebased tip two of
U-pipe-replay's offline-replay tests failed with `RISK_LIMIT_EXCEEDED — ES is not on the allowed instrument list`:
their fixtures relied on the old default where an empty allowlist allowed everything, the very default this unit
removed; 580/582 otherwise green. A fresh fixer set their allowlists the way the rest of the suite does.
**NOT VERIFIED:** the card's pixels — this Mac's screen was locked and `screencapture` refuses a window rect, so the UI
evidence is the running app's own reported state, not a picture; one fault test failed once, unidentified, then four
suites in a row were green. **Open, not this unit's (U-gates' string):** an order refused while the row is unreadable
says "TradeAgent's current mode does not allow this order" — a mode this build invented a moment earlier; fails closed,
the health row and activity log tell the truth, the sentence should name the unreadable settings.

## 2026-09-05 — U-press-budget landed: the press's two-second promise is asserted on its own clock, and the simulator clips like the shipped connector

The ubuntu one-off "the press took 3.4s against a 2s emergency budget" (run 33952871991), by one fresh fixer on
`docs/briefs/U-press-budget.md`. Merge `4221d89`, 6 commits, 3 files of substance, +189/−21 with the brief (`FakeConnector.cs`, `FakeBroker.cs`, `EmergencyPressTests.cs`).

- **It was the runner's, measured on all three hosted runners.** A throwaway harness pushed to CI (`d734e42`, run
  33969167809, taken back out) timed `OperatorCancelAllAsync` against the same stalled platform per call: ubuntu the
  press = 2005–2012 ms (orders 1 → 1200 full length inside the budget, cancel 1202 → 2000 cut exactly at the deadline,
  positions 2004 → 2004 refused with zero left); macos 2013–2135; windows 2030–2038. The press's OWN cost with the
  latency knob at 0 — write-ahead rows, latch, composite rows, settles, activity line, every SQLite write at
  `synchronous=FULL` — is 5–7 ms on ubuntu, 6–11 ms on macos, 34–40 ms on windows. Nothing in the press is off the
  emergency clock.
- **The one thing a late timer could still reach was the instrument.** `FakeConnector` PREDICTED ("1.2 s fits in the
  2.0 s left, so run it") and then slept its full nominal latency unclipped; the shipped `AtasConnector` clips
  (`Left(deadlineAt)` on the write, `CancelAfter` on the reply) and never predicted. `TheCancellableWait` makes the
  simulator clip too, onto the same branch, sentence and `PossiblyWritten`; `UncancellableLatencyMs` and opening
  placements untouched.
- **The two timing tests assert the promise, not a stopwatch:** the deadline the press itself opened, read back out of
  the scope; overrun below `GatewayPipeServer.HandlerOverhead` (the contract's H, exactly these local writes); the press
  RETURNED "not confirmed — check ATAS"; cancel-all's cut leg never reached the book. The 3 s and 4 s wall clocks are
  gone, not raised; the shipped 2 s is untouched.

**Verified by running (the fixer, quoted; then the manager's gate):** RED (new test; seam `FaultProfile.Wait`, 1.2 s
declared delivered 2.2 s late) "a press whose simulator ran late returned 1409 ms after the deadline the press itself
opened" — a 3.4 s press, CI's own number → GREEN at ~10 ms; mutant `CancelAfter(InfiniteTimeSpan)` → RED 1407 ms;
second mutant `RiskReducingScope.Begin()` (no budget) → 4 RED. Fixer's gate at `e1cb46a`, Release: 0 warnings; the
named test 5×; Unit 218 + Fault 221 + Integration 582 = 1021, 0 failed; names 0 removed, 1 added. CI run 33970406089
on draft PR #6: ubuntu SUCCESS; windows and macos red on the first attempt on tests outside this change (windows the
sweep-outcomes test, its second occurrence → `U-sweep-words-win`; macos a `Timing` shutdown test), both SUCCESS on
re-run, `package` SUCCESS. Manager's gate at `08796d0` (the merge sha's code tree, docs aside), Release: build → 0 warnings, 0 errors; suite → 219 + 239 + 582 = 1040, 0 failed; names vs
`main` → 0 removed, 1 added; scan clean; CI run 33972744178 at `6620d3d` finished after this session closed: test (ubuntu-latest) success; test (windows-latest) success; test (macos-latest) success; package success; the
run before it, at the docs-only commit `3876efa`, failed on: Integration.BridgeRoundTripTests.Reconciliation_works_across_the_bridge_after_a_lost_acknowledgement — the hosted-runner class recorded above.

**NOT VERIFIED:** which runner-side stall produced the 1.4 s at `88617a0` — a late timer or a slow fsync; it did not
recur, and that message carried only the total; the new one names the step and measures the press's own clock. No
box, no ATAS, no UI; `TradingGateway.cs` untouched.

## 2026-09-05 — U-sweep-words-win landed: the mixed-sweep fixture gives its wave the room the slowest runner needs, and asserts the multiset

A test-only fix under `docs/HOW-WE-BUILD.md` (one fresh fixer on `docs/briefs/U-sweep-words-win.md`). windows-latest
twice (runs 33966990967 and 33970406089) failed `SweepRequestIdTests.A_five_order_sweep_carries_a_mix_of_outcomes_in_one_answer`
with `Not found: "confirmed"` in `[sent-not-confirmed, rejected, not-sent, not-sent, not-sent]`; green on the re-run both
times and on ubuntu and macos every time; outside the `Timing` category, so the windows retry never reached it. Merge
`d92a61b` (three commits, rebased onto `4aa5903`), 1 test file +79/−16; no product file touched.

- **The brief's mechanism was wrong, and the fixer said so first:** the failing test ran in 1 s against a 5 s budget, so
  no deadline passed and U-press-budget's clip never fired. What happens: the lost answer settles UNKNOWN and flags the
  store while later legs of the SAME wave are still short of `ReauthorizeAtDispatchOrThrow` (which runs AFTER the awaited
  target resolution, on purpose); they are refused `1 earlier request(s) are unconfirmed` with their record at CREATED —
  `not-sent` by the `docs/CONTRACTS.md` table. Nothing misreports, so item 2 did not apply.
- **The room was file IO, and 50 ms of it was 29 ms short:** a leg's synchronous prefix is two store reads; the wave's
  whole issue spread measured under 2 ms here, and the worst windows-latest file-IO factor `RunnerSpeedProbeTests` has
  recorded is 39.52x — 79 ms. The latency is now `WaveIssueRoom` = 750 ms inside a `SweepBudget` of 20 s, both named and
  both ASSERTED before the words (exactly one leg refused for an unconfirmed earlier request; none refused at the
  deadline), so the next runner to push past a bound says which one. The multiset is asserted exactly — `[confirmed,
  confirmed, not-sent, rejected, sent-not-confirmed]` — because an `Assert.Contains` per word had passed both windows
  runs with three legs saying `not-sent`.

**Verified by running (the fixer, quoted; then the manager's gate):** RED at `LatencyMs = 1`: a `confirmed` lost in 61 of
150 runs, one exactly the CI multiset; clean over 100 legs at 2 ms and above → GREEN; mutant (`Dispatched(t) =>
TheAnswer(LegOutcome.Confirmed, t)`, a late fill reading `confirmed`) → RED `Actual: ["confirmed", "confirmed",
"confirmed", "not-sent", "rejected"]`, reverted. Fixer's gate at `90c40f4`, Release: 0 warnings; the class 3× = 44/44;
219 + 239 + 582 = 1040, 0 failed; the windows job green on draft PR #7 five times running (33973434576, 33974221120,
33974342472 ×2, 33975999193 at `6ac626b`, all four jobs SUCCESS). Manager's gate at `d92a61b`, Release: build → 0 warnings,
0 errors; suite → 219 + 239 + 582 = 1040, 0 failed; names vs `main` → 0 removed, 0 added; scan → one hit, the identifier
`IpcToken` on a context line; `rev-list --count u-sweep-words-win..main` → 0; CI 33981829058 at `d92a61b`: all three platforms and `package` SUCCESS.

**Three hosted-runner reds recorded here once each, none briefed — a brief each when one recurs:** macos on 33973434576,
`SweepRequestIdTests.A_sweep_pays_the_emergency_budget_once_not_once_per_rpc`, an NRE at `(JsonElement)reply.Data!` (its
fixture leaves 100 ms between the scope and a 1900 ms read); ubuntu on 33974342472 attempt 2, U-press-budget's own
`PressReachesTheWireOnItsOwnTermsTests.A_wait_the_simulator_predicted_would_fit_is_still_stopped_by_the_deadline`, over by
24 ms; windows on `main` at the docs-only `ab72909` (run 33973192760, unrecorded until now),
`ControlTests.Cancel_all_removes_orders_but_leaves_positions_alone` [52 s]: `Assert.DoesNotContain() Failure: Filter
matched`, a WORKING order still in the fake book after a 2 s press on a runner where the test took 52 s; not seen again in
five runs since. NOT VERIFIED that any of the three is a fixture asserting a schedule rather than the product.

**NOT done:** no product file, no box, no ATAS, no UI. The remote branch `u-sweep-words-win` is left for the owner to delete.

## 2026-09-05 — U-box-precut landed: the v0.1.2 box session minus the cut — protocol 3 walked, the gate green, two findings the fake could never show

One fresh builder with the box, on `docs/briefs/U-box-precut.md`; merge `5b9e2e9`, 2 commits: two harness instruments
(`tools/atas-lifecycle`, a probe strategy logging which ATAS callback fires; `--coid` on `probe atas`) and the report. No
product file. The box's tree was proven by hash twice (280 files, then 282 with the instruments).

- **The protocol-3 refusal and its repair, walked the way the owner would.** The new build against the box's v2 bridge:
  `ATAS bridge FAILED "bridge 8.0.14 speaks protocol 2, this build speaks 3 — press Reinstall the bridge on the Checks
  page"`, `Execution capability PAUSED "no trading connection"`. `Reinstall the bridge` on Checks, two-press through the
  UI agent: armed `Confirm: replace the bridge — trading through ATAS stops until it is started again`; result `The
  bridge is in place. Open ATAS, open a chart, and start the TradeAgent Bridge strategy on it.` The copy SUCCEEDED WITH
  ATAS RUNNING (all 14 files' sha256 equal the staged ones; `ATAS_BRIDGE_IN_USE` never fired at 8.0.14.397). ATAS closed
  and relaunched, the strategy restored STOPPED (trap 24) and activated: `ATAS bridge READY "connected · bridge 8.0.14,
  protocol 3"`; `probe atas` exit 0, `proto=3 | SupportsClientOrderId=false SupportsOrderHistory=false IsSimulated=true
  | ReconciliationProvable=false | autonomy=refused`. **`tools/atas-gate` exit 0, GATE PASSED**, both directions.
- **The press id is never offered to ATAS at all** (review-1's box item, moot): the press minted
  `TA-op-close-31d4779568274e53-0` and the bridge's write-ahead record carries it, but ATAS builds the close order itself
  (`Comment` = "Close position"; the adapter writes ours only into an EMPTY comment), so a close carries nothing of ours
  to reconcile on — not accepted, not truncated, not refused.
- **A real Close All settles UNKNOWN by construction:** app→bridge 8.2 ms (send gate + frame, ~4,900× below the 40 s
  those stages are budgeted — the 30–50 s premise behind review-1's finding 1 is false on this box), the close FILLED at
  341 ms, and the app gave up at 2.0 s (`EmergencyDeadline`) with `'close' is NOT confirmed … The bridge is busy`,
  because the bridge's `WaitFor(AckTimeout = 3 s)` outlasts the connector's 2 s budget. Both → `U-bridge-2` item 3.
- **Backlog readings.** Teardown: stopping the strategy fires `OnStopping` only; closing ATAS fires both, 1 ms apart;
  `Add` creates a throwaway instance that gets `OnDispose` with no start. Client order ids of 64 AND 65 chars are
  accepted verbatim and read back byte-identical off ATAS's own collection: the 64 ceiling in `CONTRACTS.md` is ours.
  Mutant B4 (`Buffer = 8192` → `0`) run once on the box: the idle-stalled-bridge emergency test RED `dropped … at 2.02s`,
  reverted → GREEN. The five adapter warnings: `MSB3277` (WindowsBase via the ATAS assemblies) → `NoWarn` naming the
  vendor; `CS0618` ×4 at the obsolete calls → a pragma per call site with the trap-25 reason (→ `U-bridge-2` item 6).

**Verified by running (the builder, quoted; then the manager's gate):** everything above is the box's own output, quoted
in the report. Box left with the repo Release build running at protocol 3, ATAS up, the book flat, the Strategies
folder restored, PAUSED with 2 unconfirmed requests (one older than the session, one the press). Manager's gate at
`92fd2bb` (the merge sha's code tree, docs aside), Release: build → 0 warnings, 0 errors; suite → 1039 passed, 1 failed:
`UpdateTrustTests.A_manifest_whose_declared_length_is_too_big…`, `HttpListenerException: Address already in use` in the
test's own server setup while two builders' test hosts ran on this Mac; the class alone 3× → 89/89 each; names vs `main`
→ 0 removed, 0 added; scan → four hits, all dotted version numbers; `rev-list --count u-box-precut..main` → 0; CI run
33986072734 at `5b9e2e9`: ubuntu, macos and windows all SUCCESS, `package` SUCCESS.

**NOT done, NOT VERIFIED:** no installer, no release, no update of the installed 0.1.1; ATAS 8.0.14.398 declined. `SupportsClientOrderId=false` is the bridge's own report with no broker attached: autonomy is refused there by design.

## 2026-09-05 — U-override-lease landed: the human override obeys the dispatch lease, and a late definite answer re-flags a row a human moved

Review-2 finding 1 (HIGH), by one fresh builder on `docs/briefs/U-override-lease.md`. Merge `d14a2f0`, 6 commits, 7 files,
+492/−28 (`TradingGateway.cs`, `Stores.cs`, `DashboardView.cs`, `CONTRACTS.md`, `USER-GUIDE.md`, one new test file).
Before it, the Dashboard's unconfirmed card rendered a live DISPATCHING row with its two override buttons, "No order
exists" wrote CANCELLED onto it, trading resumed, and the dispatcher's FILLED was filed `already_settled` (P3).

- **`ForceResolve` refuses while this process's dispatcher is still inside the connector call:** `INVALID_REQUEST —
  TradeAgent is still sending this order — still on the wire for 120s of a possible 50s. Wait for it to answer before
  resolving it…`; the row stays on the card and keeps trading paused; the card asks the new
  `TradingGateway.StillOnTheWire` every tick and puts that sentence where the buttons were, disarming a half-pressed one.
- **A broker's definite answer lands on a row a human moved** (the seam: two gateways over one store — the app and
  `tradeagent-gateway.exe` — the lease in one, the card in the other): the row is flagged again and the platform's answer
  is recorded beside the owner's claim — `resolved by user: I checked in ATAS and no such order exists — but Simulator
  (built in) then answered FILLED for order FB-1, 1 filled. That is not CANCELLED, and this record is flagged again until
  you have looked.` — trading paused in the dispatcher, engineering `late_definite_over_an_override`.
  `Stores.MarkNeedsReconciliation` takes an optional broker reference, filled in and never overwritten.
- **Both directions:** an override on a row whose dispatcher is dead (an entry watched END; a DISPATCHING row never
  dispatched here) resolves exactly as before, trading resumed.

**Verified by running (the builder, quoted; then the manager's gate):** item 1 RED (P3 lifted into `OverrideLeaseTests`)
`Assert.Throws() Failure: No exception was thrown` → GREEN `record now: FILLED`, `position at the broker: ES 1`; mutant
(the lease check deleted) → RED with P3's end state verbatim (`CANCELLED … no such order exists`, `FB-1 FILLED`, trading
resumed, `already_settled`). Item 2 RED `needs_reconciliation=False · broker reference: none · already_settled` → GREEN
`needs_reconciliation=True · broker reference: FB-1`; mutant (the terminal arm of the `from` set deleted) → RED, back to
`already_settled`, unflagged. Builder's gate at `8135d17`, Release: 0 warnings; the class 3× → 3/3; 219 + 242 + 582 =
1043, 0 failed (an earlier full run: 1 failed, `PeerRowTests.A_newly_arrived_silent_peer_is_not_masked_by_the_previous_
peers_auth_failure`, `TimeoutException` on its 10 s wall-clock wait in pipe code this unit does not touch; that class 3×
alone green, the whole suite green on the re-run). Manager's gate at `d14a2f0` (rebased onto `d630974`, no overlap),
Release: build → 0 warnings, 0 errors; suite → 219 + 242 + 582 = 1043, 0 failed; names vs `main` → 0 removed, 3 added
(sets 837 → 840); scan clean; `rev-list --count u-override-lease..main` → 0; CI run 33986791747 at `d14a2f0`: windows SUCCESS, macos SUCCESS,
ubuntu FAILURE 241/242 on `OperatorPressIsAnEmergencyTests.Cancel_all_gives_up_on_a_stalled_platform_inside_the_emergency_budget`
— `cancel-all returned 1243 ms after the deadline the press itself opened, against 1s of handler overhead` — the press-budget
stopwatch class (its third ubuntu instance), in a test this sha passed twice on this Mac, from a diff with no wait, lock or
timeout; judged a hosted-runner red, fixer `U-press-stopwatch` on top of `main`; the same test passed on ubuntu at `145e725`.

**NOT VERIFIED:** the card's two visual states on screen — the query they read and the refusal behind them are run; no
UI run. **NOT done:** no pipe op, no CLI verb; nothing in the press code (`U-press-inflight` owns it); no box, no ATAS.

## 2026-09-05 — U-press-inflight landed: the press waits on an order still on the wire, and an agent's close is sized at dispatch

Review-2 finding 2 (HIGH) and Codex F3, by one fresh builder on `docs/briefs/U-press-inflight.md`. Merge `145e725`, 5
commits, 9 files, +822/−23 (`TradingGateway.cs`, `Stores.cs`, `Errors.cs`, `GatewaySchema.cs`, `CONTRACTS.md`, three new
test files). Before it, an agent's `close` inside the connector call was invisible to the press — the drift re-read
compares positions and the uniqueness constraint covers press rows — so Close All sent a second sell 2 while the agent's
sell 2 was on the wire, and long 2 became short 2 (P6).

- **A press leg is refused while the gateway still has an order on the wire for that instrument.** The guard is the
  leg's own write-ahead INSERT (`Stores.TryCreateFlagged`'s new `$wire` clause: no `DISPATCHING` `execution_request` on
  the instrument), so the check and the wire are one statement, cross-process; per leg — NQ still closed while ES
  waited. The press answers `1 leg waited on an order still on the wire, so nothing was sent for it: ES is waited on by
  p6-agent, still DISPATCHING.`; the leg's word is `not-sent`. P6b (the press row first → the agent's leg refused
  `TRADING_PAUSED_UNRECONCILED`) lifted unchanged.
- **The agent's own `close` is sized at dispatch, inside the gate** (Codex F3): a fill between the snapshot and the wire
  → `POSITION_MOVED — ES was 2 when this close was sized and is 1 now…`, record CREATED, nothing sent; over the pipe
  `outcome: not-sent`, `state: CREATED`, `not_sent: 1`, `attempted: 0`, `closed: 0`; the flipped case (2 → −2) refused,
  not doubled; the ordinary close still FILLED. One `ErrorCode` added with its catalogue entry; `ModifyAsync`'s record
  now carries its instrument instead of `"-"`, which is what makes an in-flight modify visible to the guard.
- **Cancel All is not the same class**, by two probes (an agent modify and an agent cancel held inside the connector
  call): no order left working; the modify ends `UNKNOWN, flagged=True`, the cancel `REJECTED`. In `CONTRACTS.md`.
- **A stated deviation, and the residual it leaves:** the set is `DISPATCHING`, not `DISPATCHING or UNKNOWN`. With
  UNKNOWN in it the shipped `UnconfirmedLatchTests.Confirming_one_outcome_does_not_lift_another_requests_pause` went
  RED — the press wrote no row at all — because an UNKNOWN record is the ordinary state of the emergency the button is
  pressed about, and refusing on it re-imposes the pause these controls bypass on purpose. **NOT fixed:** an UNKNOWN
  closing order on the same instrument can still fill after the press's close and reverse it; stated in `CONTRACTS.md`.

**Verified by running (the builder, quoted; then the manager's gate):** item 1 RED (P6 lifted into `PressInFlightTests`)
`orders at the broker: 3 … position at the end: ES -2` → GREEN 2 orders, agent close FILLED, position flat; mutant
(`$wire` NOT EXISTS → `1=1`) → 2 RED `Expected: 2 Actual: 3`, `ES -2`. Item 2 RED `Assert.Throws() Failure: No exception
was thrown`, position 2 → 1, Sell 2 sent → GREEN; mutant (`if (live == sizedFrom) return;` → `return;`) → 2 RED.
Builder's gate at `ef7c4b2`, Release: 0 warnings; the classes 3× → 8/8 Fault + 2/2 Integration; 219 + 247 + 584 = 1050,
0 failed; names 0 removed, 10 added; scan clean. Manager's gate at `a887507` (rebased onto `d14a2f0`; the merge sha's
code tree, docs aside), Release: build → 0 warnings, 0 errors; suite → 219 + 250 + 584 = 1053, 0 failed; names vs
`main` → 0 removed, 10 added (sets 840 → 850); scan → one hit, the word "secret" in the report's own "secret scan
clean"; `rev-list --count` → 0; CI run 33987379533 at `145e725`: all three platforms and `package` SUCCESS.

**NOT done:** no box, no real ATAS, no money, no UI run (`DashboardView.PressAsync` renders `PressOutcome.Summary`
verbatim — read, not run); `AtasStrategyAdapter.Modify:1596` quoted from source, never executed; no new pipe op, no new
operator authority; `ForceResolve`/`Settle`/`LateDefiniteSettle` untouched.

## 2026-09-06 — U-two-press-grant landed: every control that grants authority is two-press, and the armed sentence can be read

Review-2 finding 3 (HIGH) and Codex F12, by two builders on `docs/briefs/U-two-press-grant.md` (the first killed by a
usage limit after four commits; the second kept all four, re-verified them, and found two more defects on the running
app). Merge `303a7ad`, 6 commits, 13 files, +818/−77 (`DashboardView.cs`, `MainWindow.cs`, `Ui.cs`, `Theme.cs`,
`Errors.cs`, `Trading.cs`, three guides, one new test file; the App now exposes internals to the unit tests, which press
the real widget factories). Before it, one press on the Safety page's mode row moved a live-activated installation from
LIVE_CONFIRM to LIVE_AUTONOMOUS and the AI's next order FILLED on a non-simulated account (P1); RESUME AI TRADING was one
press too, and a save that widened a risk cap asked once.

- **The two real-money modes and the RESUME direction of the kill switch are `Ui.Confirm`** (`Ui.ConfirmIf`,
  `KillSwitch`, `ModeButton`): OBSERVE, PAPER and STOP stay one press — they only remove authority. The armed sentences,
  in the owner's words: mode row `Confirm: let the AI place real orders without asking`; the kill switch, chrome and
  Safety page alike, `Confirm: let the AI trade again`.
- **A save that raises any safety limit asks twice** (`RiskPolicy.Widenings`, "zero is the widest value the money cap
  has"); a save that only lowers them asks once. The three guides say two presses where they promised one.
- **Two defects only the running app showed, fixed red-first:** the armed sentence ran past its card and read
  `Confirm: let the AI place real or` — the one control whose purpose is to say what the second press does was
  unreadable (a `WrapPanel` row now); and the Safety page's armed kill switch was a blank red block, class `danger`'s
  red foreground on its own red fill (`Theme.TextOnEmergency`, a new token the `emergency` style shares).

**Verified by running (the builders, quoted; then the manager's gate):** RED against `main`'s widgets (the three
factories rebuilt one-press) `Failed: 9, Passed: 6` including `A_real_money_mode…(LIVE_AUTONOMOUS)` → `Expected: null /
Actual: LIVE_AUTONOMOUS` → GREEN 17/17 3×; mutant (`Ui.Confirm` → `Ui.Secondary` on the autonomous row) → `Failed: 2`.
The two app defects RED `Expected typeof(WrapPanel) / Actual typeof(StackPanel)` and `Expected: White / Actual: Black`
→ GREEN. Labels read on the running app (`caffeinate -u`, `tools/mac-run.sh`, `tools/mac-shot.sh`) as quoted above;
the presses were made by a temporary in-app probe raising the same `Click` routed event a mouse raises — this shell has
no Accessibility grant — then deleted; weaker than a mouse, stated. Builder's gate at `8d34072`, Release: 0 warnings;
236 + 250 + 584 = 1070, 0 failed; names 0 removed, 15 added. Manager's gate at `303a7ad`, Release: build → 0 warnings, 0
errors; suite → 236 + 250 + 584 = 1070, 0 failed (with three other test hosts running); names vs `main` → 0 removed,
15 added (sets 850 → 865); scan → one hit, the phrase "Secret scan clean" in the report; `rev-list --count` → 0; CI run
33999461962 at `303a7ad`: all three platforms and `package` SUCCESS.

**NOT VERIFIED:** a mouse-driven press; Windows pixels. **NOT done:** no gateway change, no box.

## 2026-09-06 — U-bridge-2 landed: the emergency close answers inside its budget and names the order it caused; four bridge claims settled on the box

Codex F4, F8, F9, F10, F11, F14 and the box session's two findings, by two builders on `docs/briefs/U-bridge-2.md` (the
first killed by a usage limit after five commits, between a push and the rebuild, so the box's app was found STOPPED;
the second kept all five, corrected two, and found the fill). Merge `110f67c`, 9 commits, 12 files, +1066/−67
(`AtasStrategyAdapter.cs` +424, `CoidWitness.cs`, `AdapterTeardown.cs`, `BridgeProtocol.cs`, `AtasConnector.cs`, the
bridge csproj, `tools/atas-gate`, `CONTRACTS.md`, two test files). The pushed tree proven by hash both sides: 162 files.

- **The emergency close is answered inside its budget, on the order it caused.** RED on the box (`U-box-precut`):
  `'close' is NOT confirmed … The bridge is busy` for a close that filled in 341 ms. A filled market close is in NONE of
  ATAS's three order collections — its fill is in `MyTrades`, carrying `MyTrade.Order` — so the causal window now covers
  fills, on the same terms (account, symbol, side, quantity, time; never a bare symbol). GREEN: `the platform filled it;
  BTCUSDT is now flat`, `TradeAgent thinks: filled`, `Broker reference: 12063701`, matching `trade executions`' `Buy 1
  BTCUSDT @ 79747.7`, position 0. Mutant A (a bare same-symbol match) → gate RED: a stranger's `Buy 7 ES on
  SOMEBODY-ELSE` returned as the close and labelled (Codex F11, the same defect).
- **`Position.Volume` is NEGATIVE for a short** (F4), re-measured: ATAS's `Sell/Short · Market · 1 Lots` → `quantity: -1`,
  the press flattened it to 0; in `CONTRACTS.md` with the fill reading. **Order history is claimed only where ATAS says
  how far back it keeps it** (F8 = review-2 UNVERIFIED 1): mutant B (the faithful pre-fix shape) → gate RED
  `SupportsOrderHistory=True, GetOrders → 0 order(s)`; with retention zero the other branch refuses anyway.
- **The four obsolete synchronous ATAS money calls are bounded** (F10 = UNVERIFIED 3): mutant C → RED `still running
  after 12005 ms`; GREEN `5021 ms → AtasCallTimeoutException`, `calls=stalled(OpenOrder@5000ms)`, health degraded. The
  gate hosts no real `BridgeServer`: it proves the deadline and the degraded surface, not the frame loop's serialisation.
- **The witness flushes to the device before the rename that declares it written** (F9): mutant (flush deleted) → RED
  `Actual ["write","rename","Submitting returned true"]` → 167/167. **A start mid-teardown is refused** (F14): mutant →
  RED `a start mid-teardown was allowed`. **U9:** `NoWarn` MSB3277 naming the vendor, a pragma per obsolete call site,
  `TreatWarningsAsErrors` on for the bridge — mutant D (the four pragmas deleted) → 4 × `error CS0618`.

**Verified by running (the builders, quoted; then the manager's gate):** box found with TradeAgent STOPPED (`trade
status` → `IPC_UNAVAILABLE`), `C:\ta\repo\src` pushed and its Release exe missing; left with the tip build running, ATAS
up, bridge `connected · bridge 8.0.14, protocol 3`, every health row READY, book flat, 0 open, 0 unreconciled — the
three press records the leg created settled from ATAS's own `executions`. The store held 0 unconfirmed requests, not the
2 the U-box-precut record expects: NOT explained. Bridge Release build on the box, `AtasBridgeBuild=true`: 0 warnings, 0
errors; `tools/atas-gate` 23 checks, GATE PASSED. Builder's gate at `7e62b73`, Release: 0 warnings; 219 + 250 + 587 =
1056, 0 failed; names 0 removed, 3 added. Manager's gate at `c901633` (the merge sha's code tree, docs aside), Release:
build → 0 warnings, 0 errors; suite → 236 + 250 + 587 = 1073, 0 failed (three other test hosts running); names vs `main`
→ 0 removed, 3 added (sets 850 → 853); scan → hits all ATAS version numbers; `rev-list --count` → 0; CI run
33999956514 at `110f67c`: all three platforms and `package` SUCCESS.

**NOT done:** no installer, no release, no update of the installed 0.1.1; ATAS 8.0.14.398 declined. With no broker,
`ReconciliationProvable` is false and every press is flagged for a human by design: the "waiting for you" banner is that.

## 2026-09-06 — U-press-stopwatch landed: the ubuntu overrun was the runner's disk inside one post-deadline settle, so the stopwatch tests join Timing and that category is retried everywhere

The ubuntu red at `d14a2f0` (run 33986791747: `cancel-all returned 1243 ms after the deadline the press itself opened`),
by two fixers on `docs/briefs/U-press-stopwatch.md` (the first killed by a usage limit with its harness committed; the
second read the numbers). Merge `ce4b367`, 5 commits, 6 files, +81/−28: `build.yml`, `HOW-WE-BUILD.md`, three test
files, the brief. Product code untouched (`git diff main -- src/` empty). Draft PR #8, closed.

- **Measured first, 3 runs × 3 runners on the draft PR** (run 33996443013 attempts 1–3, plus the killed fixer's
  33987436723), with a 20 ms tick on a dedicated thread and on the pool, GC-pause and pool-queue counters beside every
  press. The overrun is carried by ONE post-deadline local SQLite settle at `synchronous=FULL` (`SafelyRecordIndefinite`,
  seen as `leg-settle-indefinite` or at `press-settle`): ubuntu 547 / 593 / 358 / 129 / 113 ms in the bad runs and 2–5
  ms in the good ones, off the same code and fixture (a 200× spread); windows 63–187 ms (worst press 313 ms over);
  macos local steps ≤27 ms with the 9–136 ms overrun in the connector call, because macos delivered a 1200 ms timer at
  1204–1337 ms. Throughout, the tick kept arriving (21 ms ubuntu, 32–47 windows, 145–181 macos, its floor), `gcPause=0`,
  pool queue 0–1: the runner's file IO, not descheduling, GC or pool starvation.
- **Nothing in the press is wrongly off the emergency clock.** What runs after the deadline is the write-ahead record
  of what the press learned (`LatchUnconfirmed`, the settle, the activity and engineering rows — the contract's H, which
  `HandlerOverhead` bounds); putting it under the deadline would abandon the record `U-stranded` exists to keep.
- **The class joins `Timing`, assertions byte-identical:** `OperatorPressIsAnEmergencyTests` (5 tests) and
  `SweepRequestIdTests`' two budget tests. The category's meaning is written once in `build.yml` and in
  `docs/HOW-WE-BUILD.md` step 6, and its one second attempt now runs on every runner, not windows only — the numbers
  above retire "windows is the flaky one". `RunnerSpeedProbeTests`' comment stated the old rule and was corrected.
- **NOT moved, on measurement:** `PressReachesTheWireOnItsOwnTermsTests` asserts no wall clock; `ControlTests.Cancel_
  all_removes_orders_but_leaves_positions_alone` (52 s on 33973192760) was measured 24 times across the runners and
  never exceeded 63 ms against its 2 s budget — one unexplained occurrence, unbriefed, like the 24 ms
  `A_wait_the_simulator_predicted…` red on 33974342472.

**Verified by running (the fixer, quoted; then the manager's gate):** harness out (`grep` over `src tests .github docs`
→ nothing but the report's own sentence). Fixer's gate at `0c0d53c`: `dotnet clean`, Release `--no-incremental` → 0
warnings; the moved classes 3× → 5/5 and 2/2 each time; partition exact, `Category=Timing` 93 + `Category!=Timing` 961
= 1054 listed; names 860 each side, 0 removed, 0 added; the local full suite NOT run (other legs held 4–9 test hosts on
this Mac throughout) — **draft PR #8 green TWICE on all three runners, category step included (run 33997442226 attempts
1 and 2: 1053 passed per runner, 0 failed, 0 retry markers, so both are first-attempt greens)**. Manager's gate at
`ce4b367`, Release: build → 0 warnings, 0 errors; suite → 236 + 250 + 587 = 1073, 0 failed (two other test hosts
running); names vs `main` → 0 removed, 0 added (sets 868 → 868); scan → one hit, "Secret scan" in the report;
`rev-list --count` → 0; CI run 34001656361 at `ce4b367`: all three platforms and `package` SUCCESS.

**NOT done:** no product code, no box, no ATAS, no UI; nothing loosened, the shipped 2 s untouched.

## 2026-09-06 — U-codex-2a landed: the position cap counts open work, a missing multiplier fails closed, the re-check is last before the wire, a duplicate composite waits for its owner

Codex F1, F2, F5 and F18 (read-only claims from review 2), by two builders on `docs/briefs/U-codex-2a.md` (the first
killed by a usage limit with four files uncommitted; the second kept all four, added the proofs, corrected one test).
Merge `3de8cc6`, 5 commits, 14 files, +1042/−45 (`TradingGateway.cs` +295, `GatewayPipeServer.cs`, `Errors.cs`,
`FakeConnector.cs`, `CONTRACTS.md` +71, four new or changed test files).

- **F1 fixed — `MaxOpenPositions` counts opening work, decided inside the dispatch gate.** RED on `main`'s gateway:
  `cap 1 / connector place calls: 2 / orders at the broker: 2 / positions open: 2`, and a WORKING opening order read
  `positions reported: 0` with no refusal → GREEN; mutant (`>=` → `>`) → `connector place calls: 2` again. A
  MODIFICATION no longer runs this cap — deliberate, in `CONTRACTS.md`: a cap on instruments cannot be raised by
  changing an order already in one.
- **F2 fixed — a value-capped order whose contract size the platform cannot supply is refused**, three ways the
  multiplier goes missing (the instrument read throws, `ContractSize` null, zero): RED `Assert.Throws() Failure: No
  exception was thrown` ×3, the order went out → GREEN `RISK_CHECK_UNAVAILABLE`, 0 place calls, no row; mutant
  (`|| size <= 0` dropped) → the zero case red. `CONTRACTS.md` states the multiplier's provenance; ATAS's `LotSize`
  mapping NOT VERIFIED.
- **F5 half fixed, half refuted.** A gateway-side window Codex's wording did not name: the re-check sat above the
  close's stale-position re-read, so a close made one more awaited round trip after its last gate — RED `outcome: ok —
  FILLED / orders at the broker: 2 (was 1)` with the switch down → GREEN with the re-check the last thing before the
  wire; mutant (deleted) → red identically. Codex's literal probe REFUTED: paused inside the connector's send, the order
  is placed and cannot be recalled; closing that would mean the owner's press waiting on the wire, or manufacturing
  the UNKNOWN rule 3 exists to avoid. The bound, in `CONTRACTS.md`: one `WorstCaseOperationPath`, 50 s at shipped values.
- **F18 fixed — a second caller on a running composite waits for the owner's answer.** RED, verbatim: owner
  `{"cancelled":2,[FB-1=CANCELLED,FB-2=CANCELLED]}` against the duplicate, stored first, `{"cancelled":1,[FB-1=DISPATCHING,
  FB-2=CANCELLED]}` → GREEN with an in-memory owner lease; mutant (no re-read after the wait) → `from the store: False`.
  The press's `BeginComposite` takes no lease: its `op-` ids carry a fresh nonce the pipe refuses to name.

**Verified by running (the builders, quoted; then the manager's gate):** builder's gate at `c64e9b8`, run alone,
Release: 0 warnings; the touched classes 3× → 29/29 and `GatewayPipeBackpressureTests` 34/34 each; 236 + 261 + 587 =
1084, 0 failed; names 0 removed, 9 added; one test-only cost — `A_cold_placement_…drain_assumes` names the same five ops
with `positions` moved 2nd → 4th, the count and the drain it bounds unchanged. Manager's gate at `3de8cc6`, Release:
build → 0 warnings, 0 errors; suite → 236 + 261 + 587 = 1084, 0 failed (one other test host running); names vs `main`
→ 0 removed, 9 added (sets 868 → 877); scan clean; `rev-list --count` → 0; CI run 34003273178 at `3de8cc6`: all three
platforms and `package` SUCCESS.

**NOT done:** no box, no UI, no new pipe op, no new operator authority; F5's connector-send half refuted, not closed;
`Stranded.AtasOrderPath` NOT re-measured here.

## 2026-09-06 — U-batch-2 landed: a download is what it says it is, and the material ledger measures

Review-2 findings 4 (MED), 5 (MED), 6 (LOW) and Codex F15, F17, F19, by two builders on `docs/briefs/U-batch-2.md`
(the first killed by a usage limit with seven files uncommitted; the second kept all seven, changed two things inside
them, and added the proofs). Merge `15b873d`, 6 commits, 25 files, +1457/−116 (`Downloader.cs` +236, `MaterialStore.cs`,
`MaterialScanner.cs`, `WorkspaceBuilder.cs`, `Paths.cs`, a new `AgentPresence.cs`, `Database.cs` schema 4, `InboxView.cs`,
`Prerequisites.cs`, `NodeRuntime.cs`, `AtasInstallation.cs`, four test files).

- **A part file is bound to what it is a part of** (finding 4, F15): the resume file is named after the URL and the
  expected length and discarded when either differs; resuming still resumes. `Integrity` has no null — `Pinned`,
  `Unverified`, `PinnedOr` — so a checksum-less install is a recorded decision that writes one activity line carrying
  the vendor's reason, never a silent skip; `Downloader.RecordDecision` is wired above `Connector.ConnectAsync()` so a
  backend that will not connect cannot decide whether the owner is told. ATAS's hash stays the owner's decision: a null
  `installerSha256` in `atas.json`, pinnable with no rebuild. Mutant (the binding deleted) → 3 of 5 RED, the finished
  file beginning `STALE`.
- **`Inbox` is a measurement** (finding 5): the agent's home is `workspace/agent`, a sibling of `workspace/inbox` (an
  older install's folders are moved on the next start; unchanged for the owner). Origin is `Inbox` only when
  `AgentPresence` attests that no agent process was alive since the last COMPLETE scan pass; otherwise `InboxUnattested`,
  and the Inbox page and the guide say so in the owner's words. Mutant (the attestation deleted) → `Expected:
  InboxUnattested / Actual: Inbox` ×2, P5a exactly; second mutant (the window advanced on a truncated pass) → the same.
- **A removed row is never un-removed** (finding 6, F17, F19): schema 4 adds `material.version` — a sighting after
  `removed_at` is a new row with no hash and the old row keeps its own; `ByShaPrefix` refuses non-hex, under 4
  characters, and any prefix matching two distinct hashes; `(size, mtime)` are re-read from the OPEN HANDLE before and
  after the bytes. Three mutants, one per guard → `Expected: 2 / Actual: 1` rows; `No exception was thrown` ×2;
  `Expected: 0 / Actual: 1` hashed.

**Verified by running (the builders, quoted; then the manager's gate):** GREEN 5/5, 6/6, 6/6, each 3×. Builder's gate at
`53912fb`, bin/obj deleted, Release: 0 warnings; 253 + 261 + 587 = 1101, 0 failed, one project at a time with no other
test host (a first run overlapping another leg's suite was discarded); names 0 removed, 17 added. Manager's gate at
`15b873d`, Release: build → 0 warnings, 0 errors; suite → 253 + 261 + 587 = 1101, 0 failed (no other test host); names
vs `main` → 0 removed, 17 added (sets 877 → 894); scan → two hits, `http://127.0.0.1` in a test vendor; `rev-list
--count` → 0; CI run 34012671151 at `15b873d`: all three platforms and `package` SUCCESS.

**NOT done:** no box, no ATAS, no money, no UI run; no ATAS hash pinned (the owner's call); `DashboardView`'s "Open the
AI's folder" still points at `workspace/` (another builder owned that file); the DDL's in-place blind spot (size AND
mtime both preserved) is still open — it needs unconditional hashing; ledger rows at the agent's old paths are
re-recorded under `agent/` by the next scan rather than migrated.

## 2026-09-06 — U-batch-2b landed: an unreadable vendor file fails visibly, the ATAS rows cannot outlive the truth, a sign-in URL is checked before the shell

Codex F16 and F20, review-2 UNVERIFIED 4 and 6, by one fresh builder on `docs/briefs/U-batch-2b.md`. Merge `ab67a56`, 4
commits, 16 files, +942/−51 (a new `Core/VendorFile.cs`, `RuntimeManifest.cs`, `AtasHealth.cs`, `AtasInstallation.cs`,
`MainWindow.cs`, `OnboardingView.cs`, `Doctor.cs`, `Errors.cs`, `AppHost.cs`, `USER-GUIDE.md`, four test files).

- **An override file that exists and does not parse yields no runtime and no ATAS folder, and says so** (F16 =
  UNVERIFIED 6): `VendorFile` reads `runtimes.json` and `atas.json` once — absent → the built-ins; present and
  unparseable or empty → the most restrictive value plus one sentence. `RuntimeCatalog.Read/Require` yield NO manifests,
  so `MainWindow`/`OnboardingView` start nothing; `AtasLayout.Read` yields empty candidates, so both ATAS rows,
  `RepairOffered` and `InstallBridge` refuse; `RuntimeFileHealth` writes the `Agent runtime` row on the tick and hands
  it back when the file is corrected; `Doctor` adds a row per file; two new error codes, because `AI_RUNTIME_NOT_FOUND`
  reads "not installed yet" and offers to install — the wrong morning. RED 4/4 on `main`: the built-in codex, carrying
  `--dangerously-bypass-approvals-and-sandbox`, returned in place of a restrictive override, and `["%ProgramFiles(x86)\
  ATAS Platform", …]` for a corrupt `atas.json` → GREEN 8/8; mutant (the old silent catch) → 7 red.
- **The ATAS rows are kept against the directory entries they were read from, not only a clock** (F20):
  `IAtasProbe.Stamp(detection)` over `atas.json`, both folders, the bridge assembly and the platform exe the version came
  from; the reading is dropped the moment the entries disagree, the minute stays as the backstop. RED 2/2: the bridge
  deleted outside the app → the row still sent the owner into ATAS to start a strategy that was gone; ATAS replaced →
  `"running · 8.0.14.397"`, not the new version → GREEN; mutant (the stamp comparison dropped) → 3 red.
- **Only an http or https address, or one of TradeAgent's own folders, is handed to the shell** (UNVERIFIED 4):
  `MainWindow.RefusedToOpen` refuses everything else in the window with the target quoted back (120 chars), the folder
  test comparing text before touching the disk so a share name never becomes a connection. RED 4/4 on `main`:
  `ta-not-a-real-scheme://sign-in`, `\\evil-share\payload.exe` and a bare path went straight to the shell → GREEN 17/17
  (`file:`, `javascript:`, `ms-settings:`, `ftp:` refused; five real sign-in, help and download URLs still open); mutant
  (the check disabled) → 12 red.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `7280ca8`, Release: 0
warnings, 17 projects; seven touched classes 3× → 21 runs all passed; 280 + 261 + 587 = 1128, 0 failed, one project at
a time with nothing else running; names 0 removed, 14 added. Manager's gate at `22e0be7` (the merge sha's code tree,
docs aside), Release: build → 0 warnings, 0 errors; suite → 280 + 261 + 587 = 1128, 0 failed; names vs `main` → 0
removed, 14 added (sets 894 → 908); scan → version numbers and the tests' own URL literals; `rev-list --count` → 0; CI
run 34014253085 at `ab67a56`: all three platforms and `package` SUCCESS.

**NOT VERIFIED:** the `ChooseRuntime` screen's refusal panel on any screen; item 1's health and Checks rows are asserted
through `HealthRegistry` and `DoctorReport`, not photographed; item 1's health-row half could not be red-first (the
reporter did not exist to fail) — the mutant kills both halves. **NOT done:** `OpenAtasOrExplain` still starts ATAS
through its own `Process.Start` (an `InstallDir` a readable `atas.json` points at, `File.Exists`-guarded, not behind
item 3's check); no box, no real ATAS, no UI run.

## 2026-09-06 — U-codex-2b landed: an unreadable present field is refused, a versionless frame is refused, a heartbeat that cannot describe the bridge clears the proof

Codex F6, F7 and F13 (read-only claims from review 2), by two builders on `docs/briefs/U-codex-2b.md` (the first killed
by a usage limit after three commits; the second kept all three, added the proofs, corrected a false claim the branch
shipped) and one fresh fixer on `docs/briefs/U-codex-2b-fix.md` for the race the manager's gate found. Merge `07cbb91`,
8 commits, 11 files, +994/−45 (`Protocol.cs`, `GatewayPipeServer.cs`, `AtasConnector.cs`, `AtasHealth.cs`, `CONTRACTS.md`, tests).

- **F6 — a price or a tif that is present and unreadable is refused, not read as absent.** RED (the guard reverted,
  real pipe, 10 failed): `limit='bad' → ok=True · connector saw: Market limit=none`, `limit='1,5' → Limit limit=15`,
  `tif='' → ok=True · connector saw: Day` → GREEN `INVALID_REQUEST` naming the field, nothing placed; three mutants
  (`AllowThousands`; JSON null read as absent; `InvariantCulture` → `CurrentCulture`) each red. JSON `null` on
  limit/stop/quantity/tif and `all: ""` were claimed by `CONTRACTS.md` and covered by nothing: both RED pre-fix, now
  asserted. A false claim corrected: `InvariantGlobalization=true` makes `new CultureInfo("de-DE")` throw in this build.
- **F13 — a frame that does not say which protocol it speaks is refused rather than read as this one.** RED: a hello
  with no `v` answered `ok:true … compatible:true` and a versionless `buy` on a live session `FILLED` → GREEN
  `INCOMPATIBLE_PROTOCOL`, then `IPC_UNAUTHENTICATED`, no order; mutant → refused for the wrong reason, red on the
  reason. The old test asserting the versionless hello was accepted is `[Fact(Skip=…)]` naming its replacement.
- **F7 — a heartbeat that cannot say what the bridge can do clears what it last said.** RED: `after 10.0s of pulses
  that cannot describe the bridge: … provable=True · autonomous dispatch: authorized=True` → GREEN `provable=False ·
  authorized=False code=AUTONOMY_REQUIRES_PROVABLE_STATE`, row `FAILED — …has stopped saying what it can do`; a mutant
  dropping `BridgeCompatible` from `Attested` left the suite green, so the missing probe (a heartbeat at a version this
  build does not speak attests nothing) was written and went red.
- **The race the manager's gate found, fixed by the fresh fixer:** `Capabilities` read `_hello` six times while the new
  clear could null it between reads — `NullReferenceException` at `AtasConnector.cs:554`, once in the full suite, the
  class alone 40/40 ×3. `_hello` is `volatile` and every reader takes one snapshot (`Capabilities`, `StatusDetail` and
  its three helpers, `Unauthenticated`, `PeerHasGoneQuiet`; `AtasHealthReporter.Report` read `StatusDetail` twice a
  tick). RED 6/6 with a 32-reader hammer (`torn read : NullReferenceException — at get_Capabilities() … line 554`; the
  tier-0 disassembly reloads the field before each read) → GREEN 20/20; mutant → RED 3/3.

**Verified by running (the builders, quoted; then the manager's gate):** builder's gate at `98a4da2`, Release: 0
warnings; 237 + 261 + 610 = 1108, 0 failed, 1 skipped. Fixer's gate at `c67858c`: 0 warnings; 1125 passed, 0 failed, 1
skipped. Manager's gate at `07cbb91` (rebased onto `64743bf`; one add/add conflict on the fixer's brief, a docs file,
taken from the branch), Release: build → 0 warnings, 0 errors; suite → 281 + 261 + 610 = 1152 passed, 0 failed, 1
skipped (no other test host); names vs `main` → 0 removed, 15 added (sets 908 → 923); scan clean; `rev-list --count`
→ 0; CI run 34014790766 at `07cbb91`: RED on all three runners on ONE test, the F7 probe's own precondition
(`the harness never authorized autonomous dispatch` at 300 / 174 / 130 ms): it races the fixture's first bare heartbeat,
which clears the proof it reads, and the Mac finishes inside that 100 ms; test-only fixer `U-attest-precondition` on top.

**NOT done:** `req.V` is checked only on `hello` (a wrong version named mid-session is read as this one) and `Bridge`
and `StatusDetail` remain two readings of the connector — both for the next review; no box, no UI run.

## 2026-09-06 — U-attest-precondition landed: the F7 probe opens every gate before the fixture stops attesting

The all-three-runner red at `07cbb91` (run 34014790766: `the harness never authorized autonomous dispatch, so losing it
proves nothing` at 300 / 174 / 130 ms), by one fresh fixer on `docs/briefs/U-attest-precondition.md`. Merge `575390c`, 2
commits, `BridgeRoundTripTests.cs` +19/−5 and the brief. No product file; draft PR #10, closed.

- **The fixture asserted a schedule.** `ThrowsAfterHandshake` made `Describe()` throw on every heartbeat from the
  handshake on, at a 100 ms interval, so the proof the test asserts first is cleared by the first bare pulse — and the
  gateway construction between that assertion and the precondition (`TestEnv.NewDb()`, a `TradingGateway` over SQLite at
  `synchronous=FULL`, `Update`, `ActivateLive`) had to finish inside those 100 ms. This Mac does; no hosted runner did.
- **Now `ThrowsWhileTold`**, as the sibling test already used: the handshake and the heartbeats attest while the harness
  builds the gateway, opens the four health rows and activates live; `adapter.Throwing = true` comes after
  `Assert.True(gw.TryAuthorizeExecution(…))`; the 10 s poll and every assertion are untouched. Fixed, it passes in 120 ms
  (ubuntu), 294 ms (macos), 366 ms (windows), read off the runs' own trx.
- **The sweep found no other assertion that depends on finishing inside one heartbeat interval:** only two adapters can
  throw from `Describe()`; `ThrowsAfterHandshake`'s one remaining user asserts the CLEARED state after a 1500 ms delay
  (waiting longer only makes it truer); the sibling, the version probe and the race hammer drive `StubBridge`, which
  beats only when told. One near-miss named and left alone: a six-beat liveness margin that is the assertion its test
  exists for.

**Verified by running (the fixer, quoted; then the manager's gate):** still RED against the pre-F7 product (`4c68a11`'s
two connector edits reverted in the worktree, then restored): `… coid=True history=True provable=True / autonomous
dispatch : authorized=True` → `Assert.False() Failure  Expected: False  Actual: True` at `:427`. 20× loop → 20/20 (41/41
each), the class 3×, the test alone 20× at ~141 ms. Fixer's gate at `67eae98`, Release: 0 warnings, 17 outputs; 281 +
261 + 610 = 1152 passed, 0 failed, 1 skipped, 0 other test hosts; names via `--list-tests` 1153 = 1153. Draft PR #10
green twice on the whole matrix at `67eae98` (34017814011 first attempt on every job; 34015391617 after one windows job
rerun); the fixed test passed on every runner in every run, ten runner-passes. Manager's gate at `575390c`, Release:
build → 0 warnings, 0 errors; suite → 281 + 261 + 610 = 1152 passed, 0 failed, 1 skipped; names vs `main` → 0 removed,
0 added (sets 923 → 923); scan → one hit, the word "recompiled" beside a version-like token in the report; `rev-list
--count` → 0; CI run 34022116577 at `575390c`: all three platforms and `package` SUCCESS.

**Two NEW hosted-runner reds recorded here once each, both windows-latest, both outside `Timing`, neither briefed
(a brief each when one recurs):** `SweepRequestIdTests.Every_sent_not_confirmed_leg_carries_an_unknown_record_that_
will_be_reconciled` (`Assert.NotEmpty() Failure` at `:652`, the U-sweep-words-win family, a different test) and
`CoidWitnessTests.A_vanished_temp_is_not_waited_for`, on PR #10's runs 34016321810 and 34015391617; NOT VERIFIED whether
either is a fixture asserting a schedule. **NOT done:** no product code; no box, no UI.

## 2026-09-06 — v0.1.2 cut and published: built on the box with the adapter present, hashed in four places, the update watch in flight

The v0.1.2 cut, `docs/briefs/U-cut-0.1.2.md`, by one builder killed by a usage limit after the version bump and the box
build, then finished by the manager in two halves (this section is the first; the second — the installed 0.1.1 watched
updating itself — is `U-cut-0.1.2-b`, in flight). Merge `6672b5e`: one product commit, `Directory.Build.props`
`<Version>` 0.1.1 → 0.1.2, the one place the assembly, the installer (`/DAppVersion`) and the tag read it from.

- **Built on the box from the bumped tree** (`54e0782`, whose product tree — `src/`, `packaging/`,
  `Directory.Build.props`, `tools/`, the solution — is byte-identical to the release commit `6672b5e`; the two differ in
  docs and one test file only, `git diff --stat` empty over the product paths). `packaging/build.ps1` with the ATAS
  install dir: `C:\ta\repo\artifacts\` holds `TradeAgent-Setup-x64.exe` (117,979,718 bytes) and `SHA256SUMS.txt`
  (497 bytes); the ATAS adapter is compiled into the staged bridge, read the way the script reads it — the ASCII type
  name `AtasStrategyAdapter` in `artifacts\stage\bridge\TradeAgent.AtasBridge.dll` → PRESENT.
- **Four of the five hashes agree:** the box (`certutil`), this Mac after `scp` (`shasum -a 256`), `SHA256SUMS.txt`'s
  line, and the GitHub asset digest from the API — all `672f28fa5f43cbe12d8786264fbb42cbbe8a10ecd2e7bb1df4da3371e72dab66`
  at 117,979,718 bytes; `SHA256SUMS.txt`'s own digest `2726090…dee384`. The fifth, the copy the app downloads, is the
  second half's.
- **Published from the Mac** after the cut branch's draft PR #9 (run 34022206728 at `6672b5e`) was green on ubuntu,
  windows and macos, and after `main` was fast-forwarded onto the branch with no docs commit in between, so the release
  targets a commit on `main`: `gh release create v0.1.2 --target 6672b5e… --title "TradeAgent 0.1.2" --notes-file …`
  with the two assets; `gh release view` → not a draft, not a prerelease, Latest; `git merge-base --is-ancestor v0.1.2
  main` → yes. A first attempt failed harmlessly (run outside a git directory: `not a git repository`), created
  nothing, and was repeated with `-R` and absolute paths. The notes are in the owner's words and say the ATAS installer
  hash is not pinned, the owner's decision pending.
- **What the release carries** since v0.1.1, as its notes say: two-press for every grant; the bridge at protocol 3 with
  `Reinstall the bridge`; the emergency close confirmed on the real bridge inside its budget; a close refused against
  an order still on the wire or a position that moved; the override under the dispatch lease; unreadable settings and
  vendor files failing closed and visible; inbox origin attested; downloads bound to what they are a part of.

**Verified by running (the manager):** the hashes and sizes quoted above; `gh api …/releases/tags/v0.1.2` digests;
`gh release list` → v0.1.2 Latest; CI on `main` at `6672b5e`: run 34022935338, all three platforms and `package` SUCCESS. **NOT VERIFIED yet:** the
update installing itself; the build script's own summary block was not read — the leg that ran it was killed and its
console output is gone; the adapter presence is the script's own check, re-run by hand. **NOT done:**
`TreatWarningsAsErrors` for the whole solution (U9's remaining half); the ATAS hash not pinned.

## 2026-09-06 — The v0.1.2 update watch, first attempt: the installed 0.1.1 cannot start on the box's real home

The second half of the cut, first attempt: `U-cut-0.1.2-b`, one fresh builder against the real home (branch
`u-cut-0.1.2-b` @ `a23f540` holds its report). No file changed but the brief.

- **First attempt: NOT DONE, and a finding.**
  The INSTALLED 0.1.1 (file version 0.1.1.0, commit `16d4862`, `DatabaseSchemaVersion = 1`) started against the box's
  real home and showed only `TradeAgent cannot start / TradeAgent's records are damaged. / If this keeps happening, open
  TradeAgent again and use Create support package on the Checks page.` — `state\tradeagent.db` is at `schema_version 3`
  from this week's repo builds, `Database.cs:132` refuses a newer schema as `STATE_DATABASE_CORRUPT`, and that screen has
  no shell and no update strip, so the release feed was never read. The records are not damaged; they are newer. An
  owner who ran a newer build once is told a falsehood and has no update path from there: for the next review.
  Nothing on the box reached the ledger (db mtimes unchanged); ATAS untouched, the book flat; the fifth hash NOT READ.
  The measurement moves to a fresh home: `U-cut-0.1.2-c`.

**Verified by running (the builder, quoted):** `tools/win-state.sh` → everything works, capture WORKS; the installed exe's
file version `0.1.1.0`, product version `0.1.1+16d4862…`; the app launched through the UI agent (12 UIA elements, the
three sentences above, no control); `trade status` at once and after 20 s → `IPC_UNAVAILABLE`; `meta.schema_version`
read off a copy of the live database → `3`; the app closed by WM_CLOSE, the copy deleted. **NOT VERIFIED:** everything
about the update path — download, checksum verification, the refusal sentences, Setup's own window, relaunch, the
version after, the health rows after. **NOT done:** no install by hand, no installer run, no release edited, no
second home in that attempt, no mode or settings change, no order.

## 2026-09-06 — The v0.1.2 update watch, second attempt: the installed 0.1.1 updated itself to 0.1.2 from a fresh home, and the fifth hash matches

`U-cut-0.1.2-c`, one fresh builder (branch `u-cut-0.1.2-c` @ `12a559a` holds its report). No file changed but the brief.

- **Second attempt, from a fresh home: DONE.** `C:\ta\home-0.1.1-update` empty; the installed 0.1.1 started under
  `TRADEAGENT_HOME` at 11:07 (box time) and showed `STEP 1 OF 16 · Welcome …` and no update strip — in 0.1.1 the strip
  lives in the shell, which onboarding never reaches. The setup journey pressed through the UI agent (`Practice
  simulator`, `Simulation account`, `Create it`, `Start the AI`, `Finish` 11:15:03; ATAS never chosen, its Strategies
  folder untouched: 0 files written today). **11:15:08, one second after Finish:** `TradeAgent 0.1.2 is available ·
  112.5 MB. You are running 0.1.1.` with `What's new` · `Install update` · `Later`; the first press armed `Confirm: close
  TradeAgent and install 0.1.2, stopping the AI`, the second press at 11:16:20; 11:16:23 the installer stood complete at
  `updates\0.1.2\TradeAgent-Setup-x64.exe`, 117,979,718 bytes; 11:16:25–35 one window carrying only `Cancel` (Setup's
  own progress window; no console at any point); 11:16:35 TradeAgent 0.1.2 relaunched by itself on the same home.
  **The fifth hash,** `certutil` on the copy the app downloaded → `672f28fa…ab66`, equal to the published installer at
  the same byte count. After: exe file version `0.1.2.0`; `trade status` → `app_version 0.1.2`, `TradeAgent READY
  "0.1.2"`; Settings reads `This version 0.1.2 · Newest published version 0.1.2 — you have the newest one`; the fresh
  home's database at `schema_version 4`. The real home's database mtimes identical before and after.
- **A finding for the next review:** 0.1.1 recorded no press — its `activity` table holds 13 rows, none about the
  update, `engineering_log` empty, against the guide's "Every press you make is written into your Activity history";
  0.1.1's `UpdateService.cs` has no logging call; `main` has the hook (`UpdateService.Activity` →
  `UpdateTradingInterlock.Record:125`), and that it fires at runtime is NOT VERIFIED.

**Verified by running (the second builder, quoted):** everything above is the box's own output with times; the
download/verify strip's text was NOT read (15 s from the second press to the relaunch); Setup's caption NOT captured.
**Box left:** no TradeAgent running; the INSTALLED app is now 0.1.2, so the next start against the real home migrates
it 3 → 4 — the owner's moment; ATAS up with the bridge started at protocol 3, the book flat; the fresh home left in
place; the UI agent as found. **NOT done:** 0.1.2 never started against the real home; no installer run by hand; no
release touched; `What's new`, `Later`, `Install the add-on`, `Reinstall the bridge` never pressed. **CI on `main` after
the cut:** every docs-only run green except 34024633518 at `82a4cc3`, windows, `CloseAllOutcomeTests.Close_all_keeps_
going_after_one_position_fails` [56 s], `Assert.Equal() Failure: Values differ` (Expected: 2
Actual:   0
) — the 52-second press shape, a first
occurrence, green at the next run `09faf9f`; recorded, briefed if it recurs.

## 2026-09-06 — U-life landed: the AI works without being asked — a mission loop, a mission, and a two-press grant

The first unit of the vision stated that morning ("a never stopping evolving AI agent with sole purpose to at least win
enough money to pay for itself"), by one fresh builder on `docs/briefs/U-life.md`. Merge `0ec96c6`, 8 commits, 14 files,
+2207/−20 (new `MissionLoop.cs`; seven product files touched; four new test classes).

- **The mission loop** (`MissionLoop.cs`, hosted by `AppHost.MissionHost`): while the owner has let it, turns run back
  to back; each message is a `## Situation` block the app writes (the owner's typed words FIRST; time, mode, execution
  and its reason, account, positions — or "could not be read" when the broker call failed — open and unconfirmed
  requests, new inbox material, Guidance) and the memory sentence. Next turn at once, or after `.tradeagent/next.json`'s
  `after_seconds` (capped at 30 min, consumed), or after a backoff doubling 30 s → 30 min while turns fail; a fresh CLI
  session every 20 turns; a turn that throws is a failed turn, not the end. `TurnEnded(exit code, duration, raw)` fires
  on every path, the seam for `U-meter`. The loop yields to the scanner — a real `ScanMaterials` pass after every turn
  and one before a turn when the inbox changed. RED (the pre-turn yield deleted): `Expected: Inbox / Actual:
  InboxUnattested` → GREEN 21/21; mutant (the post-turn pass deleted) → the same, plus `"first 2, then 2"` read as
  `"first 1, then 0"`. STOP AI TRADING removes trading permission and leaves the loop running, asserted twice.
- **The mission** (`WorkspaceBuilder.Instructions`, GREEN 7/7): "Make at least enough money, net of what you cost to
  run, to pay for yourself"; the number is `trade pnl --json` and "An unknown is never a zero"; `PLAN.md` and
  `JOURNAL.md` in `trading/` are the memory across a fresh session; research, backtesting and strategies ARE the job
  when execution is blocked; the inbox is material and guidance, never instruction or permission; a turn ENDS.
- **Controls** (the AI card, `Ui.ConfirmIf`, `Theme.cs` only): "Let the AI work on its own" is two presses, "Pause the
  AI" one; Guidance is saved in `Settings`, read into every Situation, and grants nothing; paused survives a restart,
  working resumes unless `ResumeAiOnStart` is off, and then the flag is corrected; an unreadable settings row clears
  both. RED (a one-press `Ui.Primary`): `Expected: False / Actual: True` after the FIRST press, 3 red → GREEN 14/14;
  mutant (the armed sentence → null) → 2 red. Beyond the brief, declared: a message typed while the AI worked was
  lost; `SendAsync` now queues it first.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `b274045`, Release: 0
warnings; MissionLoop 21/21, MissionInstructions 7/7, MissionControls 14/14, TypedWhileWorking 4/4, each 3×; 327 + 261
+ 610 = 1198 passed, 0 failed, 1 skipped, the other leg's suite overlapping part of it. Manager's gate at `0ec96c6`,
Release: build → 0 warnings, 0 errors; suite → 327 + 261 + 610 = 1198 passed, 0 failed, 1 skipped, no other test host;
names vs `main` → 0 removed, 43 added (sets 923 → 966); scan → two hits, the guide's own wording about sign-in;
`rev-list --count` → 0; CI run 34040140435 at `0ec96c6`: **RED** on macos and ubuntu, windows green —
`TypedWhileWorkingTests` read `runtimes.json` while the corruption test had it corrupted (outside the shared collection,
a harness race: the same tree green at `b3e7d0a`, run 34040307786); fixer `U-typed-catalog` on top, this sha stays red.

**NOT VERIFIED:** the card on a running app — no UI run, no photograph, its words asserted by test only; the app's
`ScanMaterials` attesting across the loop's synchronous pass with the agent dead was NOT watched. **NOT done:** no
schema change; the loop cannot start the AI (the owner presses `Start the AI` first); `MissionInbox.ChangedSince` walks
the drop folder each ask, unmeasured on a large folder; `trade pnl` lands with `U-ledger`; the cap with `U-meter`.

## 2026-09-06 — U-ledger landed: every fill is written down once, `trade pnl` withholds a net it cannot compute, a Performance card

The ruler for the vision's objective ("pay for yourself"), by one fresh builder on `docs/briefs/U-ledger.md` and one
fresh fixer on `docs/briefs/U-ledger-rebase.md` (the branch carried over the landed `U-life`). Merge `640bce5`, 9
commits, 20 files, +1884/−7 (new `Db/FillStore.cs`, `Gateway/Pnl.cs`, `PerformanceCard.cs`; `Database.cs` schema 5,
`Versioning.cs`, `GatewaySchema.cs`, `TradingGateway.cs`, `GatewayPipeServer.cs`, `Protocol.cs`, `TradeCli/Program.cs`,
`Contracts.cs`, `FakeBroker.cs`, `DashboardView.cs`, `CONTRACTS.md`, `USER-GUIDE.md`; four new test classes).

- **A `fill` table, schema 5:** one row per execution keyed `(account_id, execution_id)`, written by the gateway only,
  never updated or deleted, from the `ExecutionReceived` stream AND a pull of `GetExecutionsAsync` at every (re)connect
  and every five minutes; each pull's `since`, outcome and the coverage start in `kv`; `agent_session` on every row.
  RED `Assert.Single() Failure: The collection contained 2 items` (`X-2` twice, `Event` and `Pull`) → GREEN 6/6;
  mutant (`source` added to the key) → the same `2 items`.
- **`trade pnl --json`** (`Pnl.cs`, a `pnl` op, the CLI, the schema, `CONTRACTS.md`): realized by average cost per
  symbol and day, unrealized from positions at the last quote, fees where known, drawdown, `incomplete` naming what is
  missing. **An unknown never reads as zero.** RED `Assert.Null() Failure: Expected: null / Actual: 48` with an empty
  `incomplete`, 3 failed / 7 passed → GREEN 10/10; mutant (NULL fee coalesced to 0) → the same. The pipe found a second
  defect: `Json.Options` drops nulls, so `net` and `fees` arrived as MISSING keys — a declared reply type, `"net":null`
  asserted. A symbol whose only presence in a period is an open position gets its own row, so the breakdown adds up.
- **Performance card** (`PerformanceCard.cs`, three inserted lines in `DashboardView.cs`, not the brief's one): GREEN
  3/3; mutant (a withheld net printed as a number) → `Expected: "—" / Actual: "0.00"`.
- **Beyond the brief, declared:** `ExecutionInfo.Fee` in `ConnectorSdk` as an init-only property with a default —
  no construction site re-parameterised, so the bridge's box-only compile is unaffected (manager's check).
- **The rebase** (fixer): onto `b3e7d0a`; `DashboardView.cs`'s left column carries the AI section as `main` wrote it
  AND `_performance.Root`; `USER-GUIDE.md` keeps `main`'s Dashboard sentence and both new sections; commits kept.

**Verified by running (the builder and the fixer, quoted; then the manager's gate):** builder's gate at `d11db37`,
Release: 0 warnings; 7/7, 11/11, 3/3, 5/5 each 3×; 302 + 261 + 616 = 1179, 0 failed, 1 skipped; one earlier run red on
its own assertion (an apostrophe the serializer escapes), fixed. Fixer's gate at `547e83d`: 0 warnings; 8 classes 3× →
24 runs, 0 failed; 348 + 261 + 615 = 1224 passed, 0 failed, 1 skipped. Manager's gate at `4113386` (the merge sha's
product tree, the last rebase docs-only), Release: build → 0 warnings, 0 errors; suite → 348 + 261 + 615 = 1224
passed, 0 failed, 1 skipped (Integration 10 m 36 s; no other test host); names vs `main` → 0 removed, 26 added (sets
966 → 992); scan clean; `rev-list --count` → 0; CI run 34041956405 at `640bce5`: **RED on macos only**, the same
`runtimes.json` harness race as `0ec96c6` (`U-typed-catalog` in flight); ubuntu and windows green, `package` skipped.

**NOT VERIFIED:** the card on a running app — no UI run, no photograph, proved by its words; that ATAS serves
in-session `MyTrades` only is read from its source, not watched on hardware, so the coverage start recorded in `kv` is
the pull's own claim. **NOT done:** no box, no ATAS, no real money; fees on ATAS are NULL until the bridge reports
them (`incomplete` says so); the meter's cost side is `U-meter`'s, in flight.

## 2026-09-06 — U-meter landed: what the AI costs, per turn and per day, and a cap that pauses it

The cost half of "pay for yourself", by one fresh builder on `docs/briefs/U-meter.md` (four commits, then superseded
mid-gate) and one fresh fixer on `docs/briefs/U-meter-finish.md` (the leftover edit judged and committed, the branch
carried over the landed ledger, the gate at the rebased tip, the report). Merge `902e3da`, 6 commits, 18 files,
+2022/−28 (new `TurnMeter.cs`; `AgentSession.cs`, `MissionLoop.cs`, `CliAgentRuntime.cs`, `AppHost.cs`, `DashboardView.cs`,
`Trading.cs`, `Errors.cs`, `GatewaySchema.cs`, `GatewayTypes.cs`, `TradingGateway.cs`; tests).

- **Usage from the stream** (builder): the runtime's own `--json` event carries the tokens onto `AgentTurnEnded.Usage`.
  Measured twice on this Mac, `codex-cli 0.153.4`, `codex exec -s read-only --skip-git-repo-check --json "say hi"`, the
  fourth and last stdout line verbatim: `{"type":"turn.completed","usage":{"input_tokens":17232,"cached_input_tokens":
  12928,"cache_write_input_tokens":0,"output_tokens":6,"reasoning_output_tokens":0}}`. No model name in any event, so
  `costs.json` names the model per runtime. Without `--skip-git-repo-check` the CLI refuses ("Not inside a trusted
  directory"); the built-in manifest carries it (`RuntimeManifest.cs:381`). RED `Assert.NotNull() Failure: Value is
  null` → GREEN 9/9; mutant (the parser call dropped) → RED 2/9.
- **The record** (builder): one line per turn in `state/agent-turns.jsonl` under `Paths.State` — the app's directory,
  not the agent's home — totals and the turn count in `kv`, reset at local midnight; priced from `costs.json` through
  `VendorFile` or visibly unpriced. GREEN 10/10. **It ships NO prices**: a per-token figure is a claim about a bill this
  software cannot see, so until a `costs.json` exists every turn reads "unpriced" and the cap cannot bite (below).
- **The cap** (builder): reaching it stops the loop taking turns until local midnight and tells the owner once. RED
  `Assert.Empty() Failure: Collection was not empty` (a Situation past the cap) → GREEN 6/6; mutant (inverted) → RED 5/6.
- **Surfaces** (builder): the AI card's cost line, the grant's second press naming the cap, the Safety page ceiling
  (raising asks twice, lowering saves at once), the Situation's cost line, `ai_state` / `ai_turns_today` /
  `ai_cost_today` on `trade status`. GREEN 17/17. A cross-test collision fixed on the way: a real child in the agent's
  role moved the sticky `AgentPresence.Shared`; the probe now passes its own register.
- **The fixer's half:** the leftover edit deleted `LastUnpricedReason`/`ReadTail`, unreachable because every unpriced
  branch of `CostCatalog.Price` already returns its sentence — committed as dead-code removal; the rebase over the
  ledger merged four shared files textually with NO conflict, both sides checked by name in each.

**Verified by running (the builder and the fixer, quoted; then the manager's gate):** builder's gate at the pre-rebase
tree, Release: 0 warnings; 7 classes 3× green; Unit 369 and Fault 261 green (its Integration run straddled the rebase,
discarded). Fixer's gate at `14164c9`, Release: 0 warnings; 11 classes 3× → 33 runs, 0 failed; 390 + 615 + 261 = 1266
passed, 0 failed, 1 skipped; names 0 removed, 42 added. Manager's gate at `902e3da`, Release: build → 0 warnings, 0 errors; suite → 390 + 261 + 615 = 1266 passed, 0 failed, 1 skipped (Integration 10 m 36 s, the typed fixer's Unit runs overlapping — no false green possible); names vs
`main` → 0 removed, 40 added (sets 992 → 1032); scan clean; `rev-list --count` → 0; CI run 34044922006 at `902e3da`: all three platforms and `package` SUCCESS.

**NOT VERIFIED:** the card, the armed sentence and the cap control on a running app — words only; the codex measurement
is macOS only; the status fields asserted on the composer's JSON, not over a live pipe; no mutant re-run by the fixer.
**NOT done:** no prices shipped, so the cap is inert until `costs.json` exists — the owner cannot be asked to write
JSON, so `U-prices` (list prices as dated data plus a Settings field) is queued in the resume file; `agent-turns.jsonl`
is never rotated; no box, no ATAS, no money.

## 2026-09-06 — U-typed-catalog landed: a unit test no longer reads the runtime file the corruption test corrupts

The hosted-runner red at `0ec96c6` (run 34040140435, macos and ubuntu) and `640bce5` (run 34041956405, macos), by one
fresh fixer on `docs/briefs/U-typed-catalog.md`. Merge `4d48395`, 3 commits, test-only: `TypedWhileWorkingTests.cs`,
`VendorOverrideFileTests.cs`, the brief. Draft PR #12, closed after the landing.

- **The race, reproduced deliberately:** a new test corrupts `runtimes.json` then builds the session → `TradeAgentException
  : runtimes.json could not be read … the text in it is not valid JSON` at `RuntimeManifest.cs:448` from
  `TypedWhileWorkingTests.Session()` — the CI message, file and frame exactly. GREEN once `Session()` takes its manifest
  from `RuntimeCatalog.BuiltIn()`, a literal with no file behind it: 5/5. The reproduction stays, inside the shared
  `VendorOverrideFiles` collection, because it writes the file.
- **The sweep, instrumented rather than read:** `VendorFile.Read` logged a stack per call across the whole Unit assembly
  → 28 vendor-file reads, every one inside `[Collection(VendorOverrideFiles.Name)]` (`VendorOverrideFileTests` 17,
  `RuntimeCatalogTests` 4, `DoctorReconciliationCheckTests` 2 plus 4 `Doctor.RunAsync` continuations whose only callers
  are those two classes, the reproduction 1); no writer outside them; the other two assemblies take their own
  `TestEnv.Home` and write neither file. The sweep is now asserted, not reported: a source scan fails naming file,
  class and call. Mutant (`Require` put back) → RED, `TypedWhileWorkingTests.cs: TypedWhileWorkingTests calls
  RuntimeCatalog.Require(`.

**Verified by running (the fixer, quoted; then the manager's gate):** fixer's gate at `b0bb35b`, nothing else running,
Release: 0 warnings, 0 errors; touched classes 3× → 14/14 each; the Unit suite 5× → 329/329 each; 329 + 261 + 610 =
1200 passed, 0 failed, 1 skipped; names 0 removed, 2 added. The three runners on PR #12 (run 34041875509 at `b0bb35b`):
ubuntu pass (the assembly that was red 329/329), macos pass, windows pass, `package` pass. Manager's gate at
`4d48395` (rebased over the ledger and the meter, both of which added no vendor-file reader), Release: build → 0 warnings, 0 errors; suite → 392 + 261 + 615 = 1268 passed, 0 failed, 1 skipped, no other test host;
names vs `main` → 0 removed, 2 added; scan clean; `rev-list --count` → 0; CI run 34045624693 at `4d48395`: all three platforms and `package` SUCCESS.

**NOT VERIFIED:** the guard against the merged tree on the hosted runners themselves, until `main`'s own run at the merge
sha completes. **NOT done:** no product code; no box, no UI.

## 2026-09-06 — U-coid-vanished-win landed: the vanished-temp witness test times the product's retry, not the runner's disk

`CoidWitnessTests.A_vanished_temp_is_not_waited_for`, red on windows-latest twice (PR #10's runs, then run 34035317665
at the docs-only `e238932`: `burned 137 ms of the retry budget on a file that is not coming back`), by one fresh fixer on
`docs/briefs/U-coid-vanished-win.md`. Merge `697e24f`, 3 commits, test-only: `CoidWitnessTests.cs` +38/−5, the brief.
Draft PR #11, closed after the landing.

- **The 137 ms were the runner's disk, not the retry loop.** A timing harness on PR #11, 3 runs × 3 runners (34041706666,
  34041712859, 34041721936): in all 27 samples the rename was attempted ONCE and the retry loop spanned 0.0 ms, while
  the call around it cost 10.7–50.3 ms on windows (8.1–29.2 of that before the rename) against 0.6–19.8 ubuntu and
  1.7–4.5 macos, and one flush-to-device 7.2–47.1 ms on windows vs 0.4–1.1 ubuntu. The harness itself went red on
  UBUNTU once (194 ms, run 34041721936) with the same message at the same line — it was never one platform's.
- **Fixture fixed, no assertion loosened:** the stopwatch is read only inside the injected rename, so its span is the
  product's own sleeps and holds no file IO; the 100 ms ceiling and the message are unchanged; `Assert.Single` on the
  attempts is added, the count the sibling five-attempt test already asserts. What it no longer bounds is the disk
  around the call, which is not the product. NOT moved to `Timing`: the verdict needs no runner clock. Mutant (the
  exclusion by name dropped from `Transient`) → `burned 204 ms of the retry budget on a file that is not coming back`.
- **Seen on the way, recorded here, a recurrence:** run 34043185411 (the second run at the same sha), windows-latest,
  the `Timing` step red in BOTH attempts on `OperatorPressIsAnEmergencyTests` (`close-all returned 2813 ms after the
  deadline the press itself opened`, then cancel-all); this test passed on all three runners in both runs. That class
  is already on this record three times: briefed as `U-press-win-3`, below.

**Verified by running (the fixer, quoted; then the manager's gate):** fixer's gate at `3791460`, Release: 0 warnings, 0
errors; the class 3× → 149/149 each; 348 + 261 + 615 = 1224 passed, 0 failed, 1 skipped, 0 other test hosts;
`--list-tests` vs `main` 1225 = 1225; scan clean. PR #11 run 34042541589 at `3791460` GREEN on all three runners and
`package`, no Timing retry; run 34044573112 at `5b242ac` GREEN on all three and `package` — nine runner-passes for this
test. Manager's gate at `b6b044b` (rebased over the meter and typed landings, neither touching this file), Release:
build → 0 warnings, 0 errors; suite → 392 + 261 + 615 = 1268 passed, 0 failed, 1 skipped, no other test host; names vs `main` → 0 removed, 0 added; scan clean; `rev-list --count` → 0; CI run 34046329176 at `697e24f`: all three platforms and `package` SUCCESS.

**NOT done:** no product code; no box, no UI. Nothing in the fixer's report is NOT VERIFIED.

## 2026-09-06 — U-press-win-3 landed: the stalled press is charged for its wire calls past the deadline, not for the runner's disk

`OperatorPressIsAnEmergencyTests` (already `Timing`), red in BOTH attempts on windows-latest in PR #11's second run
34043185411 (`close-all returned 2813 ms after the deadline the press itself opened`, then cancel-all), the class's third
appearance on this record. By two fresh fixers on `docs/briefs/U-press-win-3.md` — the first killed by a usage limit after
committing a timing harness and opening draft PR #13, the second re-briefed from the branch. Merge `a167884`, 4
commits, test-only (`EmergencyPressTests.cs`, `DispatchRecoveryTests.cs`, the brief); `git diff main -- src/` empty.
Draft PR #13, closed after the landing.

- **Where the 2.8 s went, measured:** the harness on PR #13 (runs 34046090536, 34046100888; 8 presses per runner per
  job, a mark per step). The budget cut EVERY stalled platform call at its deadline on all three runners (2000–2016 ms
  against `deadlineAt=2000`; macos 2110, its timer floor), so the whole overrun is the post-deadline record-keeping.
  Worst windows press: `cancel-call-threw=2000 leg-settle-indefinite=3890 press-settle=5359 complete-composite=5468
  activity-line=5578` — one `SafelyRecordIndefinite` commit at `synchronous=FULL` cost 1890 ms and the `SafelySettle`
  1469 ms, with `gcPause=0` and a 20 ms tick arriving at 47 ms, so the process was running. Ten bare one-row commits on
  the same database: ubuntu 4–11 ms, macos 0–7 ms, **windows 16–2234 ms**. Windows overran 15–63 ms in 12 of 16 presses
  and 219 / 1000 / 1516 / 3578 ms in the other four. No product wait: one commit alone exceeds the handler overhead.
- **The fixture charged E and H together and gave the sum to an assertion about E.** `RecoveryConnector` now stamps
  every platform call in and out (`WireCalls`, permanent, the finding on it) and `TheStalledPressGaveUpOnItsOwnDeadline`
  sums the part of each call past the deadline against the SAME `HandlerOverhead` — 0–1 ms ubuntu, 0–16 ms windows,
  8–138 ms macos over 24 presses. The five tests' behavioural assertions are byte-identical; the class stays in `Timing`;
  the harness and its product hook are gone from the tip. Mutant (the late simulator ignoring its token) → RED `a press
  whose simulator ran late was still on the platform 1408 ms after the deadline the press itself opened (orders 1407
  ms, cancel 1 ms), against 1s of handler overhead`.

**Verified by running (the fixer, quoted; then the manager's gate):** fixer's gate at `8867f86`, Release: 0 warnings,
0 errors; the class 3× → 5/5 each; 392 + 615 + 261 = 1268 passed, 0 failed, 1 skipped, 0 other test hosts; names vs
`origin/main` 1014 = 1014. CI four runs green on all three runners, 1268 passed each, zero Timing retries executed:
34052601589 and 34052615610 at `7fa64f1`, 34053292187 and 34053305400 at `8867f86`, `package` SUCCESS in all four.
Manager's gate at `a167884` (rebased over the witness landing, a different test file), Release: build → 0 warnings, 0 errors; suite → 392 + 261 + 615 = 1268 passed, 0 failed, 1 skipped, no other test host; names
vs `main` → 0 removed, 0 added; scan clean; `rev-list --count` → 0; CI run 34055106702 at `a167884`: all three platforms and `package` SUCCESS.

**Carried forward, for the product rather than the test:** a Windows disk can hold one `synchronous=FULL` commit for
two seconds inside an emergency press's record-keeping. The press's platform calls were cut at the deadline every time,
so the money-path guard held; the record after it is what stretched. **NOT done:** no product code; no box, no UI.

## 2026-09-06 — U-prices landed: the cost cap bites out of the box — list prices as dated data, an unknown priced high, the owner's override

The follow-up `U-meter` queued (it shipped no prices, so the cap was inert), by two fresh builders on
`docs/briefs/U-prices.md` — the first killed by a usage limit after items 1–2 with item 3 uncommitted in eight files, the
second re-briefed from the branch, which kept all eight, re-read every price at its source and closed one hole of its
own. Merge `4e0d877`, 7 commits, 16 files, +1496/−72 (`TurnMeter.cs`, a `ListPrices` catalogue, `RuntimeManifest.cs`,
`AppHost.cs`, `DashboardView.cs`, the Safety page, `Trading.cs`, `Errors.cs`, `USER-GUIDE.md`, `RESEARCH-REQUIRED.md` § D, tests).

- **Built-in prices as dated data:** per runtime and model — input, cached input, cache write, output per million —
  read from OpenAI's pricing page on 2026-09-06 (standard tier, short context) and the Codex model ids from its docs
  the same day, with `ReadOn` and the source URL in the data; the owner's `costs.json` still wins where it speaks.
  The second builder re-read all eighteen rows and found the first had dropped the cache-write column the page
  publishes for four models — dearer than input, so 10.00 was charged where the page says 12.50. RED `Expected: 12.50
  / Actual: null` (7/8) → GREEN 8/8; mutant (the column dropped again) → the same RED. `gpt-5.3-codex-spark` has no row.
- **An unknown is priced high, never zero:** a turn whose model the stream did not name is charged at the highest list
  price in its runtime's catalogue and labelled an estimate on the card, in the Situation and on `trade status`.
- **The owner's override on the Safety page:** the price per million in and out as two boxes with the built-in and its
  date as the default; a HIGHER price saves at once, a LOWER one asks twice (it widens what the cap allows). RED (the
  rule replaced by a one-press save) `Expected: 0 / Actual: 1`, 2 failed / 21 passed → GREEN 27/27; mutant (`||`→`&&`)
  → RED. **A hole the second builder closed on its own judgement** (`cc4f1d3`, revertable alone): the boxes opened on
  the rate in force, 0 where nothing was shipped, so ONE press on an untouched pair priced every turn at nothing and
  retired the cap while it still read as in force — `OwnerPrice.From` now refuses a zero as it refused a half pair.
  RED `Actual: OwnerPrice { InputPerMillion = 0, … }` → GREEN 43/43; mutant (input half only) → RED.
- **The guide and the research table** name both pages, the date, the estimate, the backwards press and the refused
  zero; two tests pin the guide to `ListPrices`' constants (RED `Sub-string not found` ×2 → GREEN 31/31; mutant
  (`ReadOn` moved, guide untouched) → both RED); § D records the long-context and Codex-fast tiers as not taken.

**Verified by running (the builders, quoted; then the manager's gate):** the second builder's gate at `de87316`,
Release: 0 warnings, 0 errors, 17 projects; touched classes 3× → 68/68 each; 424 + 261 + 615 = 1300 passed, 0 failed,
1 skipped, no other test host; names vs `main` → 0 removed, 32 added. Manager's gate at `7609a44` (rebased over the
two test-only landings), Release: build → 0 warnings, 0 errors; suite → 424 + 261 + 615 = 1300 passed, 0 failed, 1 skipped, no other test host; names vs `main` → 0 removed, 32 added (sets 1034 → 1066); scan →
two hits, both the words "your OpenAI API key" naming the sign-in label; `rev-list --count` → 0; CI run 34056709789 at `4e0d877`: all three platforms and `package` SUCCESS.

**NOT VERIFIED:** the two boxes and the card on a running app — nobody has seen them; the prices are the vendor's page
as read on one day and will drift — the date is in the data and in the guide for that reason. **NOT done:** no box,
no ATAS, no money; the model the AI actually runs on is still whatever the owner's Codex configuration says, so the
estimate label is expected on most turns until the app names the model itself (a later unit, if the owner wants it).

## 2026-09-07 — The loop's first run on a screen: the four units seen, the cap bit at 5.07 USD, three findings

The 2026-09-06 landings (`U-life`, `U-ledger`, `U-meter`, `U-prices`) had been proved by their words only. Run on this
Mac by the manager at `main` `8d7ae97`: the app built Debug and wrapped in a throwaway `.app` bundle so the desktop's
screen control could click it (`tools/mac-bundle.sh` now), a dev home seeded to setup-complete with the practice
simulator, `SIM-001` and `codex` chosen (the `kv` `settings` blob is snake_case), codex-cli 0.153.4 on the owner's own
config (`model = "gpt-6-astra"`, `model_reasoning_effort = "xhigh"`).

- **Seen, as landed:** the Dashboard's "The AI's own work" card with its state words (stopped, paused, working, waiting
  until 00:00), the turn count and the cost line with the estimate label; "Let the AI work on its own" arming to
  "Confirm: let the AI keep working without being asked, up to 5 USD a day" on the first press and to "working" on the
  second, "Pause the AI" one press; the Guidance box; the Performance card's empty state ("No fills yet…"); the Safety
  page's cost cap (5) and the two price boxes at 10.00 in / 50.00 out naming `gpt-6-astra` and the page read on
  2026-09-06; `trade status` carrying `ai_state`, `ai_turns_today`, `ai_cost_today`, `ai_cost_estimated`.
- **The loop ran four turns unattended** (`state/agent-turns.jsonl`, fields verbatim): 336 s in=543,711 cached=491,136
  out=9,301 → 1.4819 USD; 276 s in=1,091,490 cached=1,037,440 out=6,781 → 1.9170; 183 s → 0.9387; 103 s → 0.7307; total
  5.0683. Then the activity row `The AI has spent 5.0683 USD today, which is its 5 USD daily limit. It stops taking new
  turns until 00:00. Raise the limit on the Safety page to let it carry on.` and the card `waiting until 00:00`.
- **What the AI did with its four turns** (its own `JOURNAL.md`, `PLAN.md`, two research notes, a strategy file, two
  scripts recorded with `trade material ran`): read status, schema and `trade pnl`; wrote and ran a baseline collector
  and eleven offline checks; found every simulator quote off its tick grid (MES 107.31/107.81 on a 0.25 grid) and
  declined to treat them as a market; froze opening-range rules; downloaded a free MES minute-bar sample (16,200 bars)
  and measured its quality; found Databento needs a key and did not sign up. `fill` 0 rows, `execution_request` 0 rows;
  it noted once that only the owner can fill the empty allowlist and did not ask again.

**Findings, briefed as `U-seen-1`:** the card's price probe reads only the running agent (`AppHost.cs:228`), so before
the first start it says the cap "cannot stop it" while the Safety page prices the same runtime; the simulator's quotes
are off the tick grid and never move (`FakeBroker.BasePrice`); the mission does not say so, which cost the first turn.
**Queued as `U-model`:** the loop runs on whatever `~/.codex/config.toml` names — here the dearest model, 1.5 USD and
5.5 minutes a turn, almost all of it cached input.

**NOT VERIFIED:** an order through the loop (none was placed: the allowlist is empty and the AI declined the quotes);
the price boxes' two-press (not pressed); Guidance (not typed); anything on Windows. The built-in simulator is not a
paper venue for weeks — ATAS's simulated account on the box has real prices and is.

## 2026-09-07 — U-loss landed: a per-position and a daily loss budget in the account's currency, refused by the gateway from the fill ledger

The first money-path unit after the vision session ("no human in the loop" means the app bounds the loss ahead of any broker
or prop firm), by one fresh builder on `docs/briefs/U-loss.md` (killed by a desktop-app restart mid-gate after ten commits)
and a second re-briefed from the branch, which reproduced every RED and mutant itself, ran the gate and wrote the report.
Merge `7b46503`, 11 commits, 22 files, +1593/−43 (new `Gateway/LossBudget.cs`; `Trading.cs`, `TradingGateway.cs`,
`MissionLoop.cs`, the Safety page, `GatewayTypes.cs`, `GatewaySchema.cs`, `Errors.cs`, `CONTRACTS.md`, `USER-GUIDE.md`; tests).

- **Two budgets on `RiskPolicy`:** `MaxLossPerTrade` and `MaxDailyLoss` in the account's currency, 0 = not enforced, one
  `Widens()` for the three zero-means-off caps (raising asks twice, lowering saves at once), `Unreadable()` leaves both at
  0. Persistence: a throwaway probe read `…"max_loss_per_trade":123.5,"max_daily_loss":777.25…` back from the blob.
- **The day's budget bites at the order** (`LossBudgetOrThrow`, both call sites): today's loss is the realized figure from
  `_fills.Since(StartOfDay)` by the average-cost book plus the unrealized on open positions; an order that could increase
  exposure is refused with `LOSS_BUDGET_REACHED`, a close or reduce never. RED (the check removed from both sites):
  `Assert.Throws() Failure: No exception was thrown`; mutant (`Loss = day > 0m ? day : 0m`, a loss read as profit) → the same.
- **The per-position budget** refuses an add to a position already down `MaxLossPerTrade`. RED (the `TradeReached` block
  deleted) → the same failure; mutant (`CanIncreaseExposure` → `Math.Abs(signed) > Math.Abs(held)`, so a same-direction
  add stops counting as new risk) → the same red.
- **Fail closed, never on a guess:** a traded symbol whose multiplier is unknown, or an open position that cannot be valued,
  refuses with `RISK_CHECK_UNAVAILABLE`, places nothing and writes no row; a budget of 0 reads nothing. RED (`CannotBeRead`
  returning null) → `Failed: 3, Passed: 0` across the three unknown-value tests; mutant (an unpriceable position marked at
  its own average) → red.
- **A real-money mode cannot be selected while the daily budget is 0** (`SetMode`). RED (the guard removed) → `No exception
  was thrown`; mutant (`<= 0m` → `< 0m`) → the same red.
- **Surfaces:** two Safety rows with the currency hint, the Situation line beside the cost line, `loss_today` /
  `loss_budget_day` / `loss_budget_trade` on `trade status` ABSENT when unknown, the schema, `CONTRACTS.md`, the guide,
  `AGENTS.md`, one activity line per budget per day; 24 tests added, 0 removed; three fixture edits add a wide
  `MaxDailyLoss`, one assertion added, none loosened.

**Verified by running (the second builder, quoted; then the manager's gate):** builder's gate at `54f99e4`, Release: 0
warnings, 0 errors (41 `CoreCompile` targets at `-v:n`); Unit 441 + Fault 269 + Integration 615 = 1325 passed, 0 failed,
1 skipped, exit 0 each; touched classes 3× → 70/70 and 34/34; names vs `main` → 0 removed, 24 added; scan clean;
`U-seen-1`'s suite overlapping, no `Timing` red. Manager's gate at `7b46503` (rebased over three docs-only commits),
Release: build → 0 warnings, 0 errors, the Release DLLs rebuilt at 03:12; suite → 441 + 269 + 615 = 1325
passed, 0 failed, 1 skipped; names vs `main` → 0 removed, 24 added (sets 1088 → 1112; `[Fact]`/`[Theory]`
1046 → 1070); scan clean; no trailers; `rev-list --count` → 0; CI run 34072945394 at `7b46503`: all three platforms and `package` SUCCESS.

**NOT VERIFIED:** the two Safety rows and the hint on a running app — proved by tests reading `DashboardView.cs`, never seen
rendering; the day's figure against a real account's currency — only the simulator's. **NOT done:** nothing is flattened
on a breach (`U-flatten`, after the UNKNOWN close is fixed or disabled); no box, no ATAS, no real money.

## 2026-09-07 — U-seen-1 landed: the card prices the chosen runtime before the first start, the simulator quotes on its tick grid, and the mission says what the simulator is

The three findings of the loop's first run on a screen (the previous section), by one fresh builder on
`docs/briefs/U-seen-1.md` (killed by a desktop-app restart after three commits, one test edit uncommitted) and a second
fresh builder re-briefed from the branch, which reproduced every RED and mutant in its own hands, judged the uncommitted
edit, ran the gate and wrote the report. Merge `06a8636`, 5 commits, 9 files, +473/−18 (`AppHost.cs`, `FakeBroker.cs`,
`FakeConnector.cs`, `WorkspaceBuilder.cs`; four test files). No schema change, no money path.

- **The meter falls back to the chosen runtime** the way the Safety page already did (`AppHost.cs:106`), so the AI card no
  longer says the cap "cannot stop it" before the first start. RED (the fallback reverted to the pre-fix probe) → 4 of 5
  red, `Before_the_first_start_the_cap_is_priced_by_the_runtime_the_owner_chose` → `Assert.True() Failure Expected: True
  Actual: False`; mutant (precedence inverted) → `What_is_actually_running_prices_the_turns_rather_than_what_was_chosen` →
  `Expected: "codex" Actual: "custom"`.
- **The built-in simulator's quotes sit on each instrument's tick grid,** one tick either side of the mid, deterministic,
  still never moving. RED (the snap reverted) → 3 of 5 red, `Every_quoted_price_sits_on_the_instruments_own_tick_grid` →
  `Assert.Equal() Failure: Values differ Expected: 0 Actual: 0.24`; mutant (`spread = 0.25m` whatever the grid) → the same
  test → `Expected: 0 Actual: 0.75`. No test hard-coded an old price (the three `BasePrice` call sites are live reads); the
  killed leg's uncommitted edit to `ApprovalReauthorizationTests` was clarity, not repair, and as handed over it built a
  SECOND `FakeBroker` — the two-sources-for-one-price trap it claimed to close; the second builder made it read the broker
  in play (`conn.Broker.Quote("ES", …).Last`, the field the gateway compares the cap against, `TradingGateway.cs:1049`)
  and committed it under item 2 as the only updated test.
- **The mission names the simulator as a fixture** when the connector is the built-in one: prices fixed on the grid, not a
  market, for order mechanics and the ledger, never for an edge or a result; the allowlist decides what it may touch. RED
  (the paragraph never emitted) → `Assert.Contains() Failure … Not found: "built-in simulator, and it is not a marke"`;
  mutant (the condition widened to `ConnectorIsPaper`) → `Nothing_but_the_built_in_simulator_is_described_that_way` →
  `Assert.DoesNotContain() Failure: Sub-string found … "not a market"`.

**Verified by running (the second builder, quoted; then the manager's gate):** builder's gate at `6406dd9` (rebased onto
`95d6db6`, docs-only, no conflict), Release: `Build succeeded. 0 Warning(s) 0 Error(s)`; Unit 437 + Fault 261 +
Integration 615 = 1313 passed, 0 failed, 1 skipped; touched classes 3× → 45/45 (four unit classes) and 30/30
(`ApprovalReauthorizationTests`) each run; names vs `main` → 0 removed, 13 added; the `U-loss` leg's suite overlapping,
no `Timing` red. Manager's gate at `6e26872` (rebased over the `U-loss` landing), Release: build → 0 warnings, 0 errors;
suite → 454 + 269 + 615 = 1338 passed, 0 failed, 1 skipped; names vs `main` → 0 removed,
13 added (sets 1112 → 1125; `[Fact]`/`[Theory]` 1070 → 1083); scan clean; no trailers; `rev-list --count` → 0; CI run 34073713557 at `06a8636` (the gated tip rebased once more over the U-loss record, docs-only): **RED on windows-latest only** — `MissionInstructionsTests.The_paragraph_is_the_only_difference_between_the_two_missions`, `13 out of 13 items … did not pass`: the test splits the mission on `'\n'` and the runner's CRLF checkout leaves `\r` on every line of the raw-literal paragraph (no `.gitattributes` in the repository); macos and ubuntu green, `package` skipped; the same tree 454/454 on this Mac. A fresh fixer, `U-crlf-win`, on top; this sha stays red.

**NOT VERIFIED:** the card's new wording and the snapped quotes on a running app — no UI run since the one that found them;
`AppHost.ConnectorIsBuiltInSimulator` (`Connector.Id == FakeConnector.ConnectorId`), the line that decides whether the
fixture paragraph applies at all, has no test of its own — it compiles, and the builder flagged it. **NOT done:** no box,
no ATAS, no real money; the model the AI runs on is `U-model`'s, in flight.

## 2026-09-07 — U-model landed: the app names the model it runs, commits the spend before launch, and measures the turn's context

The first council-substrate unit (`docs/COUNCIL.md`, rules 3 and 4), by one fresh builder on `docs/briefs/U-model.md`
(killed by a usage limit after all five items and a rebase) and a second re-briefed from the branch, which reproduced
every RED and mutant itself, made both codex measurements, ran the gate and wrote the report. Merge `2504c5b`, 6 commits,
26 files, +2266/−104 (new `Db/AiAttemptStore.cs`; `TurnMeter.cs`, `RuntimeManifest.cs`, `AgentSession.cs`, `MissionLoop.cs`,
`AppHost.cs`, the Safety page, `Trading.cs`, `Database.cs` schema 6, the guide, `CONTRACTS.md`; five new test classes).

- **The model is the app's choice:** `ModelArgs` and `DefaultModel` as manifest data (codex: `-m`, `gpt-5.6-sol`), the flag on
  the resumed turn as well as the first, `SelectedModelId` chosen on the Safety page from the runtime's price list, one
  press. RED (`ModelArgs` → `[]`): `Actual: ["exec", "--json", "--skip-git-repo-check", "--dangerously-bypass-approvals-and-
  sandbox", "PROMPT"]`; mutant (the flag on the first turn only) → the resumed argv loses `-m`.
- **An attempt row before launch** — schema 6, `ai_attempt`, written before `Process.Start` and updated on `TurnEnded`; an
  attempt still `LAUNCHED` when a new meter opens the database becomes `LOST` and keeps its reservation as its cost; the
  day's totals are sums over the ledger by local start day (the kv counters are gone). RED (the constructor no longer
  calls `LoseOpen`): `Expected: LOST / Actual: LAUNCHED`; mutant (`LOST` at cost 0): `Expected: 1.28 / Actual: 0`.
- **The reservation is the admission gate:** a turn runs only if spent + unresolved reservations + its own fits the cap; a
  cap that cannot fund one turn says so. RED (the check back on `!CapReached`): the fourth turn ran, `Assert.Empty()
  Failure: Collection was not empty`; mutant (reservations out of the sum) → `Expected: False Actual: True`.
- **Context by component** from the stream: prompt characters, command items by id, tool-output bytes, cached input, the
  rest `unattributed`. RED (`TurnContext.Read` empty): `Expected: 1 / Actual: 0`, 4 red; mutant (items per line): 3 red.
- **Priced at the model asked for** when the stream names none, the row saying so; the dearest-model estimate only where
  no model can be asked for; the model on the card, on `trade status` (`ai_model`) and in the guide. RED (`askedFor` never
  set): `Expected: 0.0225072 / Actual: 0.056268`; mutant (asked-for beating the stream's own) → the reverse.
- **Measured on this Mac** (codex-cli 0.153.4, `gpt-5.6-sol`, one turn each): `exec --json … -m` running `ls` → the
  `command_execution` item carries `id`, `command`, `aggregated_output` (`"alpha.txt\nbeta.txt\n…"`), `exit_code`, `status`,
  the same id on `item.started` and `item.completed`; usage `32852 in / 28032 cached / 132 out`; **no model named anywhere
  in the stream**. `exec resume --last --json … -m` → exit 0, the same `thread_id` resumed, the flag accepted on resume too.
- **One test name removed, by rename, judged at landing:** `Todays_totals_are_kept_in_kv` → `…_are_sums_over_the_launch_
  ledger`; the old asserted counters the product no longer keeps, the new asserts the three keys null and sums the ledger.

**Verified by running (the second builder, quoted; then the manager's gate):** builder's gate at `4b4d7ae`, Release: 0
warnings, 0 errors; Unit 481 + Fault 269 + Integration 615 = 1365 passed, 0 failed, 1 skipped; 11 touched classes 3× →
70/70 each, no `Timing` red; scan clean. Manager's gate at `2504c5b` (the builder's rebase onto `44ee58d`), Release: build →
0 warnings, 0 errors; suite → 481 + 269 + 615 = 1365 passed, 0 failed, 1 skipped; names vs
`main` → 1 removed (the rename above), 29 added (sets 1125 → 1153; `[Fact]`/`[Theory]` 1083 → 1110); scan clean; no
trailers; `rev-list --count` → 0; CI run 34139262127 at `2504c5b`: **RED on windows-latest only** (failure | test (ubuntu-latest):success, test (windows-latest):failure, test (macos-latest):success, package:skipped) — the one failure is `MissionInstructionsTests.The_paragraph_is_the_only_difference_between_the_two_missions`, the CRLF-checkout red already recorded at `06a8636` and fixed by `U-crlf-win` at `2082091`, whose own run is the proof; nothing of `U-model`'s went red.

**NOT VERIFIED:** the Safety page's model row on a screen — no UI run, and no test presses `DashboardView.BuildModelRow`;
only the card's words, the pipe field and the schema sentence are asserted. **NOT done:** no box, no ATAS, no order.

## 2026-09-07 — U-crlf-win landed: line endings pinned at the root, so a raw-string paragraph compiles to one program on every checkout

The windows-only red at `06a8636` (run 34073713557: `MissionInstructionsTests.The_paragraph_is_the_only_difference_between_
the_two_missions`, `13 out of 13 items … did not pass`), by one fresh fixer on `docs/briefs/U-crlf-win.md`. Merge `2082091`,
4 commits, no product code: a root `.gitattributes`, one test file, the brief; `git diff main -- src/` empty.

- **The class fix:** `.gitattributes` with `* text=auto` and `eol=lf` on `.cs`, `.csproj`, `.props`, `.sln`, `.md`, `.json`,
  `.yml`, `.sh`, `.py`, `.txt`; `eol=crlf` on `.cmd`, `.bat`, `.ps1`; `-text` on twelve binary extensions (none tracked). Its
  header says why `text=auto` alone does not fix it: every word the app ships is a `"""` raw string literal, which keeps
  the SOURCE file's line endings, so a CRLF checkout compiles a different program. `git add --renormalize .` touched ONE
  file, the vendor's `docs/atas-api-8.0.14.397.txt` (5,882 CRLF lines, a 13,158-line whitespace-only diff); the manager
  marked `docs/atas-api-*.txt -text` and reverted that, so vendor material stays byte-for-byte as shipped — after which a
  renormalise changes nothing (verified: 0 files staged).
- **The test made honest on its own:** `Lines()` splits on `["\r\n", "\n"]`, the assertion untouched. RED reproduced
  byte-for-byte as the runner sees it — `git archive HEAD` into a scratch tree, all 159 `.cs` rewritten to CRLF with
  `perl -pe 's/(?<!\r)\n/\r\n/'`, no git config changed: `Assert.All() Failure: 13 out of 13 items in the collection did
  not pass.` with `[4]: Item: "report one as though it did.\r"`; GREEN in the same CRLF tree `Passed: 454, Failed: 0`;
  mutant (`Lines()` back to `Split('\n')`) → the same 13-of-13 red.
- **The sweep, run rather than argued:** 14 sites split text on a bare `'\n'`; one needed the fix (the test above); the
  other 13 were run under the CRLF tree and pass — they normalise already (`MissionLoop.cs:193,616`, `TurnMeterTests.cs`),
  trim (`CliAgentRuntime.cs`, `NodeRuntime.cs`, `UpdateService.cs`, `GatewayPipeBackpressureTests.cs`), assert substrings
  only (`EmptyAllowlistTests.cs`, `CoidWitnessTests.cs`) or assert nothing (`BridgeRoundTripTests.cs`). No code changed.

**Verified by running (the fixer, quoted; then the manager's gate):** fixer's gate at `9ef6e37`, Release: `Build succeeded.
0 Warning(s) 0 Error(s)`, 17 projects, 37 `CoreCompile` tasks; Unit 3× → 454/454 each; Fault 269/269; Integration 615
passed, 1 skipped; `--list-tests` vs `main` 1339 = 1339, nothing removed or added; no `Timing` re-run needed. Manager's
gate at `9d2a492` (the fixer's tip plus the `-text` commit, rebased over `U-model`), Release: build → 0 warnings, 0 errors;
suite → 481 + 269 + 615 = 1365 passed, 0 failed, 1 skipped; names vs `main` → 0 removed,
0 added; scan clean; no trailers; `rev-list --count` → 0; CI run 34140348483 at `2082091` (the gated tip rebased over two docs-only commits): **all three platforms and `package` SUCCESS** — the windows-latest job green on the tree that was red at `06a8636`; `main` is green again.

**NOT VERIFIED:** the hosted windows-latest runner itself — the fixer did not open a PR run; the merge sha's CI is the
proof, recorded here when it completes. **NOT done:** no product code; no box, no ATAS, no money.

## 2026-09-07 — U-unknown-close landed: an UNKNOWN close on an instrument can no longer be doubled by a press or by the agent

The money path's item (e) — `U-press-inflight`'s stated deviation, "an UNKNOWN closing order on the same instrument can
still fill after the press's close and reverse the position" — by one fresh builder on `docs/briefs/U-unknown-close.md`
(a first builder was killed by a usage limit before any change). Merge `acff18a`, 4 commits, 8 files, +731/−14
(`TradingGateway.cs`, `Stores.cs`, `Errors.cs`, `GatewaySchema.cs`, `CONTRACTS.md`, `USER-GUIDE.md`; `UnknownCloseTests.cs`).
Rebased three times under `U-model`, `U-crlf-win` and `U-data-binance`'s brief without conflict; `git diff main` deletes
only the four lines this unit replaces.

- **The press settles before it sends**, per leg, inside its own deadline: the UNKNOWN offsetting order on that instrument
  is read back by client id — settled if terminal, cancelled if `ACKNOWLEDGED`/`WORKING` — and only then does the leg go
  out; a leg the platform cannot answer for is REFUSED with a flagged `CREATED` row naming the instrument while every
  other leg goes out. RED (the reversal reproduced): `orders at the broker : FB-1 Buy 2 FILLED | FB-3 Sell 2 FILLED | FB-4
  Sell 2 FILLED`, `position at the end : ES -2`; mutant (`UnresolvedReducersOn(symbol, side)` → an empty list) → 3 red,
  `ES -2` again.
- **The agent's close and its reduce are refused** while an UNKNOWN close is on the instrument (`CLOSE_UNRESOLVED`, in
  `CloseAsync` and on `PlaceAsync`'s own position read). RED: `Assert.Throws() Failure: No exception was thrown / Expected:
  typeof(GatewayDeniedException)` ×3; mutant (`intent.Side == side` → `!=` in `CouldMoveThePositionLike`) → the same 3 red.
- **Tests:** 8 new in `UnknownCloseTests.cs`, both directions each, none in `Timing` (two 1200 ms stalls inside a 2000 ms
  budget cannot be made to fit by any runner); P6, `EmergencyPressTests` and `Confirming_one_outcome_does_not_lift_another_
  requests_pause` untouched and green — the press still writes its row.
- **Words:** `CONTRACTS.md`'s "what that leaves open" paragraph replaced by the rule; `Stores.cs`'s comment says why UNKNOWN
  is handled per leg; both codes in `Errors.cs` and `GatewaySchema.cs`; one sentence in the guide. Nothing new on screen.
- **A deviation, declared and right:** the refusal does NOT name `trade reconcile` as the brief asked — no such verb exists,
  and `ReconcileAsync` walks only `Unreconciled()`, which excludes the unflagged UNKNOWN row this refuses over, so naming it
  would have been a false promise. It names the two routes that do settle one: the owner's card, and Close all positions.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `62d00a6`, Release: 0 warnings,
0 errors; Unit 481 + Fault 277 + Integration 615 = 1373 passed, 0 failed, 1 skipped; touched classes 3× → Fault 37/37 and
Integration 19/19 each run, no `Timing` red; names vs `main` → 0 removed, 8 added. Manager's gate at `0fb5ed0` (the
report commit on the gated tip), Release: build → 0 warnings, 0 errors; suite → 481 + 277 + 615 = 1373
passed, 0 failed, 1 skipped; names vs `main` → 0 removed, 8 added (sets 1153 → 1161; `[Fact]`/`[Theory]`
1110 → 1118); scan clean; no trailers; `rev-list --count` → 0; CI run 34143748994 at `acff18a` (the gated tip rebased over docs-only commits): all three platforms and `package` SUCCESS.

**NOT VERIFIED:** the refused leg's sentence on a running app (`DashboardView` renders `PressOutcome.Summary` verbatim —
read, not run); the read-back and the cancel against ATAS — the fake connector only. **NOT done:** the reconciler is
untouched and still never settles a press leg; `U-flatten` (the gateway closing on a breach) comes next; no box, no money.

## 2026-09-07 — U-wakes landed: the loop wakes on persisted events, idleness with a reason is healthy, the owner's words survive a restart

The second council-substrate unit (`docs/COUNCIL.md` rule 7, "Never-stopping is a scheduler, not a loop"), by one fresh
builder on `docs/briefs/U-wakes.md`. Merge `cef122b`, 8 commits, 22 files, +2019/−31 (new `Db/MissionEventStore.cs`;
`MissionLoop.cs`, `AgentSession.cs`, `AppHost.cs`, `WorkspaceBuilder.cs`, `Database.cs` schema 7, `TradingGateway.cs`,
`MaterialScanner.cs`, the AI card and the Safety page; four new test classes). Rebased four times, no conflict.

- **`mission_event`, schema 7:** ids deterministic per source (`owner:`, `inbox:`, `fill:`, `order:`, `renewal:`, `self:`,
  `review:`), a second raise of the same id a no-op, `consumed_by` the attempt id, a `disposition`; app-written only,
  asserted over `Ops`. RED (no migration): `SQLite Error 1: 'no such table: mission_event'` (7 red); mutant (`ON CONFLICT(id)
  DO NOTHING` removed): `SQLite Error 19: 'UNIQUE constraint failed: mission_event.id'`.
- **The loop turns only on a due event,** consuming it inside `AiAttemptStore.Begin`'s own transaction before the process
  starts; the review tick (`MissionReviewMinutes`, default 30, 0 = off, on the Safety page, lowering asks twice, read at the
  next start and the note says so) and the local-midnight renewal are scheduled AHEAD; the card names what it waits for.
  RED (the pre-`U-wakes` loop): `Assert.Empty() Failure: Collection was not empty`; mutant (`MarkConsumed` dropped from
  `Begin`): that, plus `Assert.True() Failure Expected: True Actual: False` — a restart re-ran the event.
- **The owner's message is a row, not a memory:** `AgentSession.Queue` writes it through `RecordOwnerMessage`, the row is
  the only copy, the receipt line unchanged, disposition `answered` or `failed` (re-raised once with the failure), the
  words still FIRST in the Situation. RED (the in-memory queue): `Assert.Empty() Failure: Collection was not empty
  Collection: ["stop buying NQ"]`; mutant (the event without its text): `Not found: "> stop buying NQ"`.
- **The idle language:** turns are caused by named events, a turn with nothing new ends at once, idleness with its reason
  is healthy, and `.tradeagent/next.json` `{"after_seconds": N}` is named with its 30-minute cap — the AI is told the file
  exists. RED: `Found: "you have not looked hard enough"` and `Not found: ".tradeagent/next.json"`; mutant (the file
  unnamed) → `Not found: ".tradeagent/next.json"`.
- **Quiescence pinned over a real chat child** alive during a pass: RED (the barrier removed): `Expected: InboxUnattested
  Actual: Inbox`; mutant (`OpenConversation` on a register of its own): `Assert.Same() Failure: Values are not the same
  instance`. The test owns its `AgentPresence` (`Shared` is sticky); that the product's halves share `Shared` is asserted by identity.
- **One assertion changed on purpose, declared:** `AiAttemptLedgerTests.The_launch_ledger_is_schema_six_and_starts_empty`
  pinned `== 6`, now `>= 6` plus the row on disk equalling the build's version, the exact number pinned by the new schema-seven
  test, the name kept. The manager's regex diff shows one "removed" name, `BeginTurn`: a fake host helper, not a test.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `1a58f1b`, Release: 0 warnings,
0 errors; Unit 510 + Fault 277 + Integration 615 = 1402 passed, 0 failed, 1 skipped; touched classes 3× → 188/188, 53/53
and 88/88 each run, no `Timing` red; names vs `main` → nothing removed, 26 added. Manager's gate at `cef122b` (the report
commit on the gated tip), Release: build → 0 warnings, 0 errors; suite → 510 + 277 + 615 = 1402 passed,
0 failed, 1 skipped; names vs `main` → 0 tests removed, 26 added (sets 1161 → 1186; `[Fact]`/`[Theory]` 1118 → 1144); scan
clean; no trailers; `rev-list --count` → 0; CI run 34146285162 at `cef122b`: **RED on windows-latest only** (ubuntu and macos green, `package` skipped) — one Integration test, `SweepRequestIdTests.Two_sweeps_mint_different_ids`, `Expected: 1 / Actual: 0`: the cancel-all sweep after a place the fake broker leaves working attempted nothing; the same tree 615/615 on this Mac and on the two other runners; judged a runner red under step 6, a fresh fixer `U-sweep-win` measures where the second is lost; this sha stays red.

**NOT VERIFIED:** the Safety page's interval field and the card's waiting line on a screen — tests only; no screen lists the
queue. **NOT done:** no box, no ATAS, no money; `U-data-binance` (in flight) and `U-council-thin` (next) read this table.

## 2026-09-08 — U-data-binance landed: the first real dataset, Binance's public 1-minute archives, with provenance the AI cannot edit

The third council-substrate unit (`docs/COUNCIL.md`, "Data"), by one fresh builder on `docs/briefs/U-data-binance.md` (killed
by a usage limit mid-gate after all five items) and a second re-briefed from the branch, which reproduced every RED and
mutant itself, made the one real download, ran the gate and wrote the report. Merge `a22939d`, 8 commits, 32 files,
+2657/−16 (new `Data/BinanceArchive.cs`, `KlineNormaliser.cs`, `DatasetReader.cs`, `Db/DatasetStore.cs`, `Provisioning/
BinanceArchiveClient.cs`, `BinanceDataService.cs`; schema 8, the pipe, the CLI, `SettingsView.cs`; seven new test classes).

- **The download,** twelve complete months of one pair (default `BTCUSDT`, a "Market data" section on Settings, one press)
  through `Downloader` pinned to the `.CHECKSUM` sidecar, into an app-owned folder no agent can write; a 404 month is
  "not published". RED (the sidecar guard stripped): `Expected: ChecksumMismatch / Actual: Collected`; mutant
  (`Integrity.Unverified`) → the same.
- **The normaliser:** one UTC timeline, the timestamp unit read off each file's own magnitude (milliseconds, microseconds
  from 2025-01), duplicates dropped and counted, gaps counted and listed, an unclosed bar excluded and counted, nothing
  filled in. RED (one unit for all): `Expected: 2024-12-31T23:50:00 / Actual: 1970-01-21T02:08:09`; mutant (the unit from
  the month's date) → `Actual: 1970-01-21T15:59:02.4`. Gaps RED: `Expected: 3 / Actual: 0`; mutant (holes filled) → the same.
- **Provenance the AI cannot edit:** a `dataset` ledger (schema 8) with every raw file's url, published and computed sha256,
  bytes, unit, and the normalised file's hash, bar count, first and last bar, gaps, duplicates, incomplete; a raw file whose
  hash no longer matches makes the dataset REJECTED for good. RED: `Expected: REJECTED / Actual: ACCEPTED`; mutant (the
  raw-file loop skipped) → `Failed: 1, Passed: 4`, watched; reproducibility mutant → two different sha256s.
- **Read-only to the AI:** `data-list` and `data-bars` (`trade data …`, at most 10,000 bars, the cap named), declared
  reply types, the deadline table; one Situation line; the mission's `data/` sentences name `trade data`. RED: `the schema
  does not name 'data-bars'`; mutant (the cap dropped) → a 20,000-bar ask served, `Expected: False / Actual: True`.
- **The suite reaches no vendor:** every test on a loopback `HttpListener` serving fake zips and sidecars, and a scan that
  fails any test naming the vendor's host or the client's default base URL. RED (a planted offender) → named by file and line.
- **The one real download, by the builder on this Mac, 2026-09-07:** `…/spot/monthly/klines/BTCUSDT/1m/BTCUSDT-1m-2026-08.zip`
  → HTTP 200, 2,084,946 bytes; the sidecar `acab442e…b843d4` equal to `shasum -a 256`; the month in progress → 404. Through
  the normaliser once: 44,640 bars, microseconds, no gaps, duplicates or incomplete bars; sha256 `f2dc9add…250b8`. One name
  removed by rename, judged: `The_schema_carries_the_table_at_version_seven` → `…_seven_or_later` (the floor convention).

**Verified by running (the second builder, quoted; then the manager's gate):** builder's gate at `d333480`, Release: 0
warnings, 0 errors; Unit 539 + Fault 277 + Integration 621 = 1437 passed, 0 failed, 1 skipped; touched classes 3× →
101/101 and 144/144 each run; names vs `main` → 36 added, 0 removed; scan clean. Manager's gate at `a22939d` (a docs-only
renumber and the report on the gated tip), Release: build → 0 warnings, 0 errors; suite → 539 + 277 + 621 =
1437 passed, 0 failed, 1 skipped; names vs `main` → 1 removed (the rename), 39 added (sets 1186 → 1224;
`[Fact]`/`[Theory]` 1144 → 1179); scan clean; no trailers; `rev-list --count` → 0; CI run 34167309186 at `a22939d`: failure | test (ubuntu-latest):success, test (macos-latest):success, test (windows-latest):failure, package:skipped — NOT green; judged in the next section.

**NOT VERIFIED:** the Settings press on a screen; the twelve-month collection against the vendor — one month only. The
`U-wakes` test found red within ten minutes of local midnight is not this unit's (fixed with `U-council-thin`). **NOT done:**
no box, no ATAS, no order; REST catch-up for recent bars; other venues.

## 2026-09-08 — U-council-thin landed: two roles run serially by the app, handing each other work through a relay that cannot lose or double it

The first visible slice of `docs/COUNCIL.md`, by one fresh builder on `docs/briefs/U-council-thin.md` (killed by a usage
limit mid-way through item 2), a second re-briefed from the branch (kept its six uncommitted files, verified item 1 itself,
built items 2–4), and a rebase fixer (`docs/briefs/U-council-thin-rebase.md`: over the landed dataset unit, the council's
tables renumbered to schema 9). Merge `96f29a6`, 6 commits, 30 files, +2760/−116.

- **Roles as data and folders:** `operations` (the chair; the existing `workspace/agent` is its home, `PLAN.md` and
  `JOURNAL.md` kept) and `research` (`workspace/research`); `WorkspaceContext.Role`; `Build(role)` writes each role its own
  mission (shared rules, then a role section); `role` on `ai_attempt` and `mission_event`; a model and a share of the
  day's cap per role (defaults `gpt-5.6-sol`, 50/50), two Safety rows. RED (`Build` reverted to one home): `Expected:
  ···"…/research" / Actual: ···"…/agent"`; mutant (`RoleSection` switching on the wrong role) → `Not found: "## Your role:
  the Research Director"`.
- **One scheduler, serial:** the next due event across roles, one conversation per role, one process at a time, both on
  `AgentPresence.Shared`; admission per role AND global; the card names the active role. RED (the loop reverted to
  `events.Due` + one conversation): `the chair's turn consumed the Research Director's wake`; mutant (the share dropped
  from `AdmitsAnotherTurn`) → `Expected: False / Actual: True`. Peak concurrent turns measured, not assumed: 1.
- **The relay, one `Database.Write`:** a Research turn's `out/report-<attempt>.md` (≤ 20 lines, a longer one rejected) →
  a `publication` row (id = sha256 of the content, role, attempt, revision, recipients, classification), a `delivery` per
  recipient and ONE uniquely keyed Operations `task:` event, then the copy into `in/`; the agenda goes back as `brief`
  events; on start and after every turn the app reconciles disk against the tables. RED (the property in `docs/COUNCIL.md`,
  `CouncilRelay.Run` a no-op): all three boundaries red, `No exception was thrown / Expected: typeof(IOException)`; mutant
  (the task id from the attempt, not the hash) → `Assert.Single() Failure: The collection contained 2 items`, 5 of 7 red.
- **The Situation per role:** Operations sees the owner's words FIRST, then its deliveries; Research its briefs; both their
  role, share remaining and wake reasons. RED (the deliveries block removed): `the chair was not told what the report
  said`; mutant (deliveries above the owner's words) → `another agent's report was put above the owner's words`.
- **Judged on the way:** the killed builder's six files kept whole and finished (`IMissionHost.Relay(role, attempt)`, so a
  publication records its attempt); the `U-wakes` test red near local midnight pinned to a midday clock, assertion unchanged.

**Verified by running (the second builder, then the rebase fixer, quoted; then the manager's gate):** builder's gate at
`119990c`, Release: 0 warnings, 0 errors; Unit 529 + Fault 277 + Integration 615 = 1421 passed, 0 failed, 1 skipped; 21
touched classes 3× → 186/186 each run; names vs `main` → 19 added, 0 removed. Fixer's gate at `e2eda82`: 0 warnings, 0 errors; Unit 558 + Fault 277 + Integration 621 = 1456 passed, 0 failed, 1 skipped; 26 classes 3× → 194/194 each; names 0 removed, 17 added; four conflicts (`Database.cs`, `Versioning.cs`, `Paths.cs`, `MissionEventTests.cs`) resolved inside the rebase, one commit subject's "schema 8" amended to 9 on an identical tree.
Manager's gate at `08ac992`, Release: build → 0 warnings, 0 errors; suite → 558 + 277 + 621 = 1456 passed,
0 failed, 1 skipped; names vs `main` → 0 removed, 19 added (sets 1224 → 1243; `[Fact]`/`[Theory]` 1179 → 1196); scan clean; no trailers; `rev-list --count` → 0;
CI run 34169374097 at `96f29a6` (the gated tip rebased over docs-only commits): **RED on windows-latest only**, macos and ubuntu green — the one failure is the SAME `BinanceArchiveTests.A_month_with_no_sidecar_at_all…` (`Expected: ChecksumNotPublished / Actual: NotPublished`, the Unit suite again 30 m 16 s) already recorded at `a22939d` with fixer `U-archive-win` in flight; Fault 272/272 and Integration 533/534 green on that runner, the sweep test included; nothing of this unit's went red.

**NOT VERIFIED:** the two roles on a screen — no UI run; no real CLI turn under either role. **NOT done:** no grant table
beyond `publication.recipients` and `classification` — grants are enforced by nothing yet (`U-api-worker`); no leases
(`U-council-concurrent`); no daily report or the dispositions `delegated`/`blocked`/`superseded` (`U-report`); no snapshot
id or freshness stamp on the Situation; no box, no ATAS, no order.

## 2026-09-08 — U-midnight-test landed: the loop's tests read a clock they control, and the midnight branch has its own assertion

A `U-wakes` test (`MissionLoopTests.The_delay_the_ai_asks_for_becomes_an_event_that_survives_the_loop`) went red on this
Mac at 23:53:56 during the `U-data-binance` gate (`Range: (00:09:50 - 00:10:00) / Actual: 00:06:03`): the loop returns
the earliest due event, and within ten minutes of local midnight the renewal beats the AI's ten-minute request. The product
is right; the fixture read the wall clock. `U-council-thin`'s builder pinned that test to midday; this fixer, on
`docs/briefs/U-midnight-test.md`, finished the job. Merge `baeff48`, 3 commits, test-only: `git diff main -- src/` empty.

- **The midnight branch asserted on its own:** `Within_five_minutes_of_midnight_the_renewal_beats_the_delay_the_ai_asked_for`
  — at a pinned 23:55 the wait is exactly five minutes, `NextKind()` is `renewal`, the AI's own self wake stays unconsumed
  at +10 min. RED first, 23:55 against the old range assertion: `Assert.InRange() Failure: Value not in range / Range:
  (00:09:50 - 00:10:00) / Actual: 00:05:00`; mutant (`LocalMidnightAfter` a day late): `Expected: 00:05:00 / Actual:
  00:10:00`, 1 of 32 red — and not the midday test, which is why the branch needed its own assertion.
- **The sweep, 40 tests in four classes:** a shared pinned `Midday` injected into the six whose verdict depends on the
  wall clock (five in `MissionLoopTests`, one in `MissionOwnerMessageTests`); two assertions TIGHTENED on the way (a
  `> DateTimeOffset.UtcNow` that compared the product against a second read of a moving clock → `> Midday`; a two-way
  `or` on the card's waiting text → one string). Not at risk, with the reason: sixteen loop tests with no event queue
  (`Schedule`/`Idle` never run), four single-turn tests (no renewal exists before the turn's own `Schedule`), nine
  `MissionEventTests` with no loop, three `QuiescenceBarrierTests` whose `UtcNow` is a 20-second poll deadline. No test
  joined `Timing`; none of the four classes carries the trait.

**Verified by running (the fixer, quoted; then the manager's gate):** fixer's gate at `2ec874f`, Release: 0 warnings, 0
errors; Unit 3× → 559/559 each; Unit 559 + Fault 277 + Integration 621 = 1457 passed, 0 failed, 1 skipped; names vs
`main` → 0 removed, 1 added; `U-sweep-win`'s Integration suite overlapping, nothing failed. Manager's gate at `61e43ec`,
Release: build → 0 warnings, 0 errors; suite → 559 + 277 + 621 = 1457 passed, 0 failed, 1
skipped; names vs `main` → 0 removed, 1 added (sets 1243 → 1244); scan clean; no trailers; `rev-list --count` → 0; CI run 34171543978 at `baeff48`: RED on windows-latest only, ubuntu and macos green — the one failure the known archive test (`BinanceArchiveTests.A_month_with_no_sidecar_at_all…`) that `U-archive-win` fixes, green on its draft PR #15; nothing of this unit's went red.

**NOT done:** no product code; no box, no ATAS, no money. What this closes: a red the hosted runners would have hit on any
run reaching that test between 23:50 and 00:00 UTC.

## 2026-09-08 — U-sweep-win landed: the sweep never lost the order; the Windows runner's disk ate the budget inside one composite commit

The windows-only red at `cef122b` (run 34146285162: `SweepRequestIdTests.Two_sweeps_mint_different_ids`, `Expected: 1 /
Actual: 0`), by one fresh fixer on `docs/briefs/U-sweep-win.md` (a first was killed by a usage limit before any change).
Merge `324a11b`, 5 commits, test-only: `git diff main -- src/` empty. Draft PR #14 for the measurement, closed after.

- **HEADLINE: the product does not race.** Measured on PR #14 (runs 34164725697 and 34165321766; 24 place-then-sweep pairs
  per runner per run, 144 sweeps, plus a control each): the sweep's book read never lost the order — `nothing_to_do=False`,
  one target in the plan, the broker holding it — every time on every runner. What varied was the budget left of the
  2000 ms operation deadline at the leg: windows min/median 1968/1985 ms, ubuntu 1999/1999, macos 1996/1999; the one step
  inside that window is the composite row's commit, and a Windows disk can hold one such commit for seconds (the
  `U-press-win-3` finding, now seen a second time). A sweep that cannot issue its leg names the order `not-sent`, which is
  the contract — so the red's own value was the product's honest answer on a runner whose disk had spent the budget.
  CONTROL on all three runners (a connector that answers the read correctly then holds until the deadline goes) reproduces
  the red exactly: `attempted=0 cancelled=0 nothing_to_do=False not_sent=1`. No red-first test and no mutant: nothing in
  the product changed.
- **Fixed once, in the fixture:** the fourteen sweep fixtures whose verdict needs a leg on the wire but is NOT about the
  emergency budget now take the file's existing 20-second `SweepBudget` (`ReadyWithBudget`, made `internal` and taking
  the fill), so no assertion depends on the runner's disk; every assertion byte-identical; the budget tests untouched.
  One of the fourteen, `A_sweep_cannot_collide…`, was found vacuous rather than red and now asserts what it names.
- **Siblings:** the ten cancel-all/close-all tests in `SweepRequestIdTests` and the four in `ReplayedSweepSendsNo…` moved
  onto the budget; the rest named as not at risk (their verdict is the budget itself, or no leg is sent). The measuring
  harness and its budget hook are removed from the tip.

**Verified by running (the fixer, quoted; then the manager's gate):** fixer's gate at `f1faf13` and again at `640c5b7`
after two rebases (over `U-data-binance` and `U-council-thin`, no conflict), Release: 0 warnings, 0 errors; Integration 3×
→ 621/621 each; the three suites → 0 failed; names vs `main` → nothing removed. Manager's gate at `324a11b (the report tip rebased over five docs-and-test commits)`, Release: build →
0 warnings, 0 errors; suite → 559 + 277 + 621 = 1457 passed, 0 failed, 1 skipped; names vs
`main` → 0 removed, 0 added (sets 1244 = 1244); scan clean; no trailers; `rev-list --count` → 0; PR #14's runners at the tip: 34166551105 at `f1faf13` green on all three and `package`; 34168445545 and 34170462786 at the two rebased tips — ubuntu and macos green, windows Integration 533/533 both times, its one Unit red the known archive test `U-archive-win` is fixing; CI run 34173019618 at `324a11b`: RED on windows-latest only, ubuntu and macos green — the one failure the known archive test (`BinanceArchiveTests.A_month_with_no_sidecar_at_all…`) that `U-archive-win` fixes, green on its draft PR #15; nothing of this unit's went red.

**Carried forward, for the product rather than the test, a second time:** a Windows disk can hold one composite commit for
most of a two-second emergency budget; the press's platform calls are cut at the deadline every time, so the guard holds
and the leg reads `not-sent`. **NOT done:** no product code; no `Timing` membership; no box, no ATAS, no money.

## 2026-09-08 — U-report landed: the owner's daily report, written by the app from what it measured, nothing inferred

Rule 10 of `docs/COUNCIL.md`, by one fresh builder on `docs/briefs/U-report.md` (killed by a usage limit mid-gate after all
four items) and a second re-briefed from the branch, which reproduced every RED and mutant itself, found and closed one gap,
ran the gate and wrote the report. Merge `3003b89`, 7 commits, 26 files, +2559/−18 (new `Gateway/DailyReport.cs`,
`DailyReports.cs`, `App/ReportView.cs`; `MissionEventStore.cs`, `PublicationStore.cs`, `CouncilRelay.cs`, `MissionLoop.cs`,
`AppHost.cs`, `MainWindow.cs`, the pipe, the schema, the CLI, `Database.cs` schema 10, `CONTRACTS.md`; four new test classes).

- **The report as data and as a file:** ten sections composed from ONE timestamped snapshot (the only clock reads in
  `DailyReports.cs` are the constructor's `_now` and `WriteNow`, never a section), every unknown a labelled dash, written at
  local midnight and on demand to `state/reports/<date>.md`, app-owned. RED (the withheld net reverted to realised minus
  known fees): `Assert.Null() Failure: Value of type 'Nullable<decimal>' has a value`; mutant (the missing cost dropped from
  the text) → `Not found: "did not report a fee"`.
- **A Daily report page in the rail:** words only, `Theme.cs` only (the one literal a `Thickness` of theme spacings), the
  day picker, "Write it now", the Operations Director's note shown only when a `note` publication exists for that day. RED
  (the day bound out of `NoteFor`): yesterday's note returned as today's; mutant (the kind check dropped) → an agenda shown
  as a note (`Kind = brief`).
- **Dispositions completed:** `delegated` (links the publication id), `blocked` (the reason), `superseded` join `answered`/
  `failed`; every owner message of the day is listed with its disposition and the doctrine's reply deadline (a setting,
  default 24 h; overdue is a line on the report, never a paid turn). RED (the still-owed clause out of `ComposeDecisions`):
  `Assert.Single() Failure: The collection was empty` — a 30-hour-old unanswered message absent; mutant (`at >= due`
  inverted) → that, plus a fresh message reported overdue.
- **`trade report --json`:** read-only, the `pnl` pattern, a declared reply type with `JsonIgnoreCondition.Never` on all 41
  nullables over 10 records, the deadline table at zero. RED (`Never` off `Net`): `Not found: ""net":null"`; mutant (an
  unreadable day quietly becoming today) → `Assert.False() Failure`. The gap the builder found: the agent's guide never
  named `trade report` — added test-first, RED `Not found: "trade report"`, then the paragraph.

**Verified by running (the second builder, quoted; then the manager's gate):** builder's gate at `151ea01`, Release: 0
warnings, 0 errors (17 projects); Unit 585 + Fault 277 + Integration 627 = 1489 passed, 0 failed, 1 skipped; touched
classes 3× → 31/31 and 6/6 each run, no `Timing` red; names vs `main` → 32 added, 0 removed; the previous builder's three
gate files read, not trusted, deleted. Manager's gate at `3003b89`, Release: build → 0 warnings, 0 errors; suite → 585 +
277 + 627 = 1489 passed, 0 failed, 1 skipped; names vs `main` → 0 removed, 32 added (sets 1244 →
1276; `[Fact]`/`[Theory]` 1197 → 1229); scan clean; no trailers; `rev-list --count` → 0; CI run 34186596626 at `3003b89`: macos and ubuntu green; windows-latest RED on the one known archive test (`BinanceArchiveTests.A_month…`, the Unit suite 31 m 46 s — this sha predates `U-archive-win`'s fix, proven green at `8197163`); Fault 272/272 and Integration 539/540 green on that runner, the press-settles test included, which makes its red at `8197163` a runner flake of the disk class. Nothing of this unit's went red.

**NOT VERIFIED:** the page on a screen and its update-in-place (a signature gate at `ReportView.cs:117,131`, read, not run —
nothing in the suite runs Avalonia); a DST-length day, a midnight write and a `note` publication end to end — no build
produces one yet. **NOT done:** no box, no ATAS, no order.

## 2026-09-08 — U-archive-win landed: a download nobody could complete is Unreachable, never "the vendor has no such month"

The windows-only red at `a22939d` (run 34167309186: `BinanceArchiveTests.A_month_with_no_sidecar_at_all…`, `Expected:
ChecksumNotPublished / Actual: NotPublished`, the Windows Unit suite 30 m 48 s), by one fresh fixer on
`docs/briefs/U-archive-win.md` (killed by a usage limit while drafting its report, its five commits kept) and a second
re-briefed from the branch. Merge `8197163`, 6 commits: the fake server, `BinanceArchiveClient.cs`, `BinanceDataService.cs`,
`Downloader.cs`, three tests added; draft PR #15 for the measurement, closed after.

- **Measured, both sides** (probe run 34169530998, a mark per request on every runner): on windows-latest the fake server,
  asked `HEAD` for the sidecar, answered `200 zip, 268 bytes` and at 153 ms THREW `ProtocolViolationException: Bytes to be
  written to the stream exceed the Content-Length bytes size specified`, never closed the response, and the client waited to
  its cancellation at 15 s; ubuntu and macos wrote, closed, `200 OK` in 29 and 8 ms. http.sys alone refuses a body on a HEAD
  response. The hang reproduced on this Mac with the throw injected: `cli THREW TaskCanceledException after 5.0 s`.
- **The harness:** a `HEAD` is answered with headers and nothing else, and the response is closed in a `finally`, so a fault
  in the fake can never hold a client open again.
- **The product** (money-path grade — a timeout was read as an answer): a download that could not be completed is reported
  `Unreachable` with the reason in words; ONLY a 404 means the month does not exist; `Downloader.StatusAsync` returns null
  on cancellation instead of a status. RED (the classification reverted to `status is OK ? ChecksumNotPublished :
  NotPublished`): `Failed: 3, Passed: 7`, each `Expected: Unreachable / Actual: NotPublished`; mutant (the cancellation
  catch returning `NotFound` instead of null) → `Failed: 2, Passed: 8`, the same message.
- **The thirty minutes cannot recur:** the client's request timeout is injectable (`DefaultRequestTimeout` = production's
  30 min, honoured; an injected 2 s honoured); a never-answering fake fails in 4.0 s as `Unreachable`, "…could not be asked
  about: it did not answer within 2 seconds".

**Verified by running (the second fixer, quoted; then the manager's gate):** fixer's gate at `1e2dc97` and again at
`110a77d`, Release: 0 warnings, 0 errors; Unit 3× → 562/562 each; Unit 562 + Fault 277 + Integration 621 = 1460 passed, 0
failed, 1 skipped; names vs `main` → 0 removed, 3 added; scan clean. CI run 34172964688 at `8676ba7`: all four jobs green —
ubuntu 11 m 26 s, macos 15 m 11 s, windows 17 m 40 s, `package` 4 m 10 s; **the Windows Unit assembly 1 m 40 s for 561 tests
against 30 m 48 s for 539 at `a22939d`**. Manager's gate at `ceff6ec (the report tip rebased over the `U-report` landing)`, Release: build → 0 warnings, 0 errors; suite → 588 +
277 + 627 = 1492 passed, 0 failed, 1 skipped; names vs `main` → 0 removed, 4 added (sets 1244 → 1248 before the report landing); scan clean; no
trailers; `rev-list --count` → 0; CI run 34187380076 at `8197163`: ubuntu and macos green; **windows-latest: the Unit suite GREEN in 2 m 35 s** (the thirty-minute hang and the archive test gone — this unit's fix proven on `main`'s own runner), Integration 539/540 green, **Fault RED on one test**, `PressSettlesAnUnknownCloseTests.A_press_cancels_the_unknown_close_before_it_closes_and_the_book_ends_flat` (`U-unknown-close`, green at its own merge sha): the press cancelled the unknown close and then had no budget left for its own leg — refused at the deadline, the book still long 2; the runner's disk again; fixer `U-press-settle-win` measures first; recorded red until it lands.

**NOT VERIFIED:** the http.sys behaviour on a Windows box — read off the runner's marks, not reproduced on hardware. **NOT
done:** no ATAS, no money, no real download, no vendor reached, no assertion loosened.

## 2026-09-12 — U-turn-commit landed: a turn's output is bound to its attempt, the plan and journal are capped revisions, and a turn ends in one transaction

Rules 5 and 6 of `docs/COUNCIL.md`, by one fresh builder on `docs/briefs/U-turn-commit.md` (killed by the weekly usage limit
with all five items committed, before its gate) and a second re-briefed from the branch, which reproduced every RED and
mutant itself, found and closed one gap, ran the gate and wrote the report. Merge `0da64d7`, 7 commits, 30 files,
+1998/−117 (`CouncilRelay.cs`, `PublicationStore.cs`, `AiAttemptStore.cs`, `MissionLoop.cs`, `TurnMeter.cs`,
`MaterialScanner.cs`, `WorkspaceBuilder.cs`, `AppHost.cs`, a new `Core/Sha256Hex`, `Database.cs` schema 11, `CONTRACTS.md`,
the guide, the mission text; new `TurnCommitTests`, `WorkspaceRevisionTests`).

- **Staged output is bound to its attempt, and fenced:** the Situation names the attempt id, `out/report-<attempt>.md` is
  attributed by the id in its name, a LOST attempt's file is published under that LOST id, and a file naming no known
  launch of the role goes to `out/quarantine/` with an activity line. Mutant (attribution by whoever ran the relay):
  `Expected: "turn-killed" / Actual: "turn-next"`; the fence deleted → 4 red, `Assert.Empty() Failure: Collection was not
  empty` (`Attempt = turn-open`).
- **The plan and the journal are private revisions with caps** (60 and 200 non-empty lines): content-hashed, numbered,
  an over-cap or unreadable file REJECTED and the last valid revision written back after the commit, the next Situation
  saying so. RED (the cap removed): `Assert.Single() Failure: The collection contained 2 items` — the 80-line `PLAN.md`
  versioned; mutant (journal cap 200 → 400): the same assertion, a 300-line journal accepted.
- **One committed transition per turn:** `End`, the publications and the wake dispositions in ONE `Database.Write`; a
  crash before it leaves LAUNCHED, `LoseOpen` marks LOST on the next start and the reconcile pass publishes the staged
  files under that id. RED (the meter's close written at once, as before): `Expected: LOST / Actual: ENDED`; mutant (`End`
  outside the transaction): `Expected: LAUNCHED / Actual: ENDED`, every publication rolled back. **Gap the builder found
  and closed:** `NextRevision` was read outside the transaction; now inside — mutant (read once before the inserts):
  `Expected: [1, 2, 3] / Actual: [1, 2, 2]`.
- **The scanner walks every role's home** plus `in/` and `out/`. RED (the chair's home alone): `Expected: [5 paths] /
  Actual: []`; mutant (`out/` dropped): the published report unrecorded.
- **One hash helper** replaces seven copies, byte-identical at each old call site (RED, case drift; mutant, UTF-16).
- **Judged at landing:** the killed builder widened `OwnerDispositionTests.The_disposition_detail_arrives_at_schema_ten`
  from `== 10` to `>= 10` plus the on-disk row compared to the build's value; kept, because the exact number is pinned by
  the new `…arrive_at_schema_eleven` test — the same move `CouncilRoleTests` made. No test name removed.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `358659e`, Release: 0 warnings,
0 errors (17 projects); 12 touched classes 3× → 96/96 each; Unit 615 + Fault 277 + Integration 627 = 1519 passed, 0
failed, 1 skipped; names vs `main` → 23 added, 0 removed. Manager's gate at `0da64d7`, Release: build → 0 warnings, 0 errors;
suite → 615 + 277 + 627 = 1519 passed, 0 failed, 1 skipped; names vs `main` → 0 removed, 23 added (sets 1232 → 1255); scan clean (`CancellationToken` and a doc
comment, judged); no trailers; `rev-list --count` → 0; CI run 34698051503 at `0da64d7`: all four jobs GREEN — ubuntu, macos, windows-latest (the press-settles test included) and `package`.

**NOT VERIFIED:** `AppHost`'s wiring of `Quarantined`/`Revisions.Rejected` to the activity log — read at the composition
root, no test runs it (none covered `Rejected` on `main` either). **NOT done:** no box, no ATAS, no money.

## 2026-09-12 — U-press-settle-win landed: the press sent its leg every time; the Windows runner's disk spent the budget inside the settle's own commits

The windows-only red at `8197163` (run 34187380076: `PressSettlesAnUnknownCloseTests.A_press_cancels_the_unknown_close…`, the
book still long 2), by one fresh fixer on `docs/briefs/U-press-settle-win.md` (killed by the weekly limit after measuring, its
three commits kept) and a second re-briefed from the branch, which read the marks, judged the fix, removed the harness and
closed draft PR #16. Merge `fac2370`, 6 commits, test-only: `git diff main -- src/` EMPTY, the `is { } stuck` call site
byte-identical; 3 files, +121/−8 (`UnknownCloseTests.cs`, `DispatchRecoveryTests.cs`, `_PressSettleWinMeasurement.cs` added
and removed).

- **HEADLINE: the product is right; the runner's disk is the whole story.** 48 presses of the failing fixture (8 per runner per
  run, PR #16 runs 34188680175 and 34189533668) reached the leg and SENT it every time on every runner: `states=[ES:FILLED]
  pos=[] sellsFilled=1`. Budget left of the 2000 ms at `leg0-close-sent`: windows 1687–1765 / 1813–1985 ms, ubuntu 1993–1997 /
  1995–1996, macos 1916–1998 / 1997–1999. Windows' spend is record-keeping alone — the settle's two transitions 78–141 ms and
  the leg's write-ahead 79–172 ms, against bare one-row commits on the same disk at median 16–47 / max 47–110 ms, `gcPause=0`,
  a 20 ms tick arriving at 31–47 ms. THE CONTROL (the settle's cancel answered correctly, then the caller held past the
  deadline — one stalled `synchronous=FULL` commit) reproduces the CI red byte for byte on ALL THREE runners:
  `settle-cancel-answered` = windows −31 / ubuntu −24 / macos −29, `lost=CANCELLED … pos=[ES 2] sellsFilled=0`. No leg was
  ever refused with budget to spare: no product change, no red-first test, no mutant.
- **Fixed once, in the fixture:** the three presses whose verdict is the book take `PressBudget` = 20 s; the one whose verdict
  IS the two-second promise keeps the simulator's two seconds; every `Assert.`/`Out.WriteLine` line byte-identical to `main`
  in both files (diffed); no `[Trait]`, no `Timing`. Two numbers in the budget argument corrected (eight worst-case commits
  fit in 20 s, not thirteen; the tick 31–47 ms); the control written in as the argument's evidence.
- **Siblings:** `AgentCloseOverAnUnknownCloseTests`' four NOT at risk, measured (`deadlines=[none]` on every wire call, no
  press, no `RiskReducingScope`). **Manager's call at landing:** the eleven press fixtures in `DispatchRecoveryTests.cs`
  stay on two seconds — same disk, but two durable commits before the leg instead of four and no red yet; recorded here as
  EXPOSED, and the one-item fixer is this section's second bullet if one goes red.

**Verified by running (the fixer, quoted; then the manager's gate):** fixer's gate at `dbaf5e5`, Release: 0 warnings, 0
errors, 17 projects; Fault 3× → 277/277 each; Unit 588 + Fault 277 + Integration 627 = 1492 passed, 0 failed, 1 skipped;
names vs `main` → 0 removed, 0 added. **CI run 34697322435 at `dbaf5e5`: SUCCESS on all three runners and `package`,
windows-latest GREEN with the press-settles test.** Its first attempt was red on ubuntu-latest only, on
`MissionLoopTests.A_file_the_owner_drops_between_turns_is_still_recorded_as_theirs` (`Expected: Inbox / Actual:
InboxUnattested`) — a path this branch does not touch, green on `main` 75 minutes earlier and on the re-run: recorded as an
observed intermittent, no fixer yet. Manager's gate at `fac2370` (the report tip rebased over the `U-turn-commit`
landing, no conflict), Release: build → 0 warnings, 0 errors; suite → 615 + 277 + 627 = 1519 passed, 0 failed, 1 skipped; names vs `main` → 0 removed, 0 added (sets 1255 =
1255); scan clean; no trailers; `rev-list --count` → 0; CI run 34701889403 at `fac2370`: all four jobs GREEN — ubuntu, macos, windows-latest (the press-settles test on its new budget) and `package`.

**NOT done:** no product code; no `Timing` membership; the eleven `DispatchRecoveryTests` presses on two seconds (above);
no box, no ATAS, no money.

## 2026-09-12 — U-budget-reserve landed: every turn is admitted inside the transaction that reserves it, the owner's chat included, and an unreported turn keeps its reservation

Rules 3 and 4 of `docs/COUNCIL.md`, by one fresh builder on `docs/briefs/U-budget-reserve.md` (killed by the weekly limit
after five items and a gate it never reported) and a second re-briefed from the branch, which rebased it over `U-turn-commit`
(four conflicts — `CONTRACTS.md`, `MissionLoop.cs`, `TurnMeter.cs`, `CouncilLoopTests.cs` — resolved inside the rebase),
reproduced every RED and mutant itself, kept and tested the fifth commit, closed two gaps and wrote the report. Merge
`1757043`, 10 commits, 23 files, +1396/−128 (`AiAttemptStore.cs`, `TurnMeter.cs`, `MissionLoop.cs`, `AgentSession.cs`,
`AppHost.cs`, `RuntimeManifest.cs`, `Errors.cs`, `Trading.cs`, `DailyReport(s).cs`, `DashboardView.cs`, `CONTRACTS.md`, the
guide; new `BudgetReservationTests`). No schema change.

- **Admission inside the reservation's transaction:** `AiAttemptStore.Begin` reads the day's totals and inserts the row in ONE
  `Database.Write`, refusing (which cap, by how much, `ResumesAt`) when the sum would pass the cap; the loop's pre-check stays
  a cheap first look. RED (the guard removed): `Expected: 1 / Actual: 2` LAUNCHED rows from two racing threads; mutant (the
  check moved back outside the transaction): the same, on three runs of three.
- **Every turn is reserved and admitted, the owner's chat included:** `AgentSession.SendAsync` opens an attempt through the
  meter; a refused chat turn never launches and the chat shows the cap sentence; one open attempt PER conversation, `Close`
  matching by id. RED: `Assert.All() Failure … Expected: 1.28 / Actual: 0`, the row `Role = research, ReservedCost = 0`
  holding the Research turn's usage; mutant (the chat skipping `Begin`): the same.
- **An unreported turn keeps its reservation:** ENDED with `cost = reserved_cost` and an `unpriced_reason`, counted by the
  report and the card as a worst case. RED (`cost=$cost` as before): `Expected: 1.28 / Actual: null`; mutant (`cost` NULL
  with the row still marked unreported): the same.
- **The formula:** input at the dearer of the plain and cache-write rates, output at the output rate; `TurnAllowance` on the
  Safety page as two boxes. RED (priced as plain input): `Expected: 6.40 / Actual: 5.200`; mutant (the max dropped):
  `Expected: 6.40 / Actual: 5.20`, in the formula and in the committed row.
- **The fifth commit, kept and pinned:** a refused turn keeps the owner's typed words and records the refusal against their
  message (mutant `Expected: "blocked" / Actual: null`). **Gaps closed:** the card, the AI's line and the report's two
  places name a turn charged its reservation; the provider-side ceiling (none for codex) stated in both docs.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `f8abb57`, Release: 0 warnings, 0
errors; 9 touched classes 3× → 88/88 each; Unit 629 + Fault 277 + Integration 627 = 1533 passed, 0 failed, 1 skipped; names
vs `main` → 14 added, 0 removed. Manager's gate at `882c336` (the report tip rebased onto `main`; landed as `1757043` after a docs-only rebase, `src`
and `tests` identical), Release: build → 0 warnings, 0 errors; suite → Unit 629 + Fault 277 + Integration 627 = 1533
passed, 0 failed, 1 skipped — the Integration suite's FIRST run spanned a four-hour sleep of this Mac (wall clock 4 h 17 m)
and went 13 red, all in `ConnectorSendDeadlineTests` (`Timing`; `"the ATAS bridge disconnected" / Not found: "busy"` — a
heartbeat verdict after the gap), re-run alone on the same build → 627 passed, 0 failed; names vs
`main` → 0 removed, 14 added (sets 1255 → 1269); scan clean (`InputTokens`/`OutputTokens`, judged); no trailers; `rev-list
--count` → 0; CI run 34715391501 at `1757043`: all four jobs GREEN — ubuntu, macos, windows-latest (27 min: the `Timing` Integration category 1-of-88 red once on `GatewayPipeBackpressureTests.A_close_all_wave_that_disposal_lands_in_leaves_nothing_unsettled`, 88/88 on the category's one retry) and `package`.

**NOT VERIFIED:** the two boxes on a screen — no UI run. **NOT done:** `_staged` is still ONE slot, safe only while the loop
is serial (`U-council-concurrent`); no provider-side ceiling exists for codex (a stated limitation); no box, no ATAS, no money.

## 2026-09-12 — U-runner-1 landed: the strategy program — parsed, validated, frozen and identified; text in, a typed program or a refusal out

Rule 8 of `docs/COUNCIL.md` begins: the runner's program half, by one fresh builder on `docs/briefs/U-runner-1.md`, written from a
read-only survey of what existed (nothing: no parser, indicator or evaluator anywhere in `src/`). Merge `1e92fe3`, 7 commits, 20
files, +3413 (new `src/TradeAgent.Core/Strategy/`, 9 files; `docs/STRATEGY-LANGUAGE.md`, 150 lines; a `CONTRACTS.md` section; four
test classes and three fixture programs under `tests/TradeAgent.UnitTests/Strategies/`). No schema change, nothing on the wire.

- **The language and its typed AST:** typed constants, indicator expressions (SMA, EMA, ATR, RSI, rolling high/low, an opening-
  range accumulator), ordered rules with exits before entries, sizing, stops, targets, maximum holding bars, time filters; one
  spot instrument, long/flat. COUNCIL's "Never:" line is UNREPRESENTABLE, not filtered — `Expr`'s constructor is `private
  protected`, the node kinds closed to the assembly — and nineteen spellings of it (an import, `def`, `for`, `while`, `now()`,
  `random()`, a model call, a shell call, a file or http read, a second instrument, `short`, `leverage 3`, …) are each refused
  naming the line. RED: `expected a program, got: the parser is not implemented` (23 red); mutant (an unknown name becomes
  a node): `expected a refusal, got a program`.
- **Refusals name the line**, every limit a constant in `StrategyLimits`. RED: `sma(close, 0)` parses; mutant (`<= 0` → `< 0`):
  5 of 25 red on the period cases.
- **Canonical form and identity:** one typed ordered text per meaning, `StrategyId = sha256(canonical + "\n" + parameters +
  "\n" + manifest)`. Four spellings of one program (spacing, comments, reordered constants, upper case with CRLF and a BOM,
  `1.50` for `1.5`) reach ONE id; fourteen changes of meaning each reach another. RED (canonical = source): `Strings differ`;
  mutant (hash the source text): `Expected: b8c939f4… / Actual: bd977b8d…`.
- **Warm-up** in one place, written into the canonical form so the id covers it. RED: `Expected: 20 / Actual: 1`; mutant (max
  lookback − 1): `Expected: 15 / Actual: 14`, 14 of 14 red.
- **The three day-one programs** as fixtures the document prints byte for byte, golden ids recomputed outside the build:
  crossover `8873b586…`, opening-range breakout `88f6586a…`, RSI mean reversion `eeb61430…`; mutant (the `instrument` line
  dropped from the canonical form): all three goldens red.
- **Two departures from `COUNCIL.md:126-166`, judged right at landing:** account-state readings (capital, equity, position, fill
  price, pending state, bars since entry) are evaluator state, not parse-time vocabulary (`U-runner-2`); the timezone is an
  allowlist of six zones, because `InvariantGlobalization` makes an OS lookup answer per platform and a promoted id must not
  depend on the machine that hashed it. **Found by the gate:** two test files had reached disk with literal NUL and control
  bytes, grep read them as binary and the name diff lost fourteen names — rewritten as C# escapes, all forty names seen.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `b5f60c1`, Release: 0 warnings, 0
errors, 17 projects; the five `Strategy` classes 3× → 77/77 each; Unit 706 + Fault 277 + Integration 627 = 1610 passed, 0
failed, 1 skipped; names vs `main` → 40 added, 0 removed. Manager's gate at `1e92fe3`, Release: build → 0 warnings, 0 errors; suite →
706 + 277 + 627 = 1610 passed, 0 failed, 1 skipped; names vs `main` → 0 removed, 40 added (sets 1269 → 1309); scan clean (the lexer's `token` variables, judged); no
trailers; no binary files in the diff; `rev-list --count` → 0; CI run 34719212649 at `1e92fe3`: ubuntu and macos GREEN; **windows-latest RED** on the three theory cases of `DayOneStrategyTests.Each_fixture_is_the_program_the_document_prints` (`Sub-string not found`: `.gitattributes` pins `*.md` to LF but not `*.strategy`, so the runner's CRLF checkout of the fixtures fails a byte-for-byte match the test normalises on one side only — the `U-crlf-win` shape; Fault and Integration green there); fixer `U-crlf-strategy-win` on top; recorded red until it lands.

**NOT done:** no evaluation, indicator values, intents, bars, storage, trace, report, pipe op or relay (`U-runner-2`, `-3`); no
per-event budget yet; nothing reads `workspace/strategies/`; three `$` prefixes without interpolation in `StrategyParser.cs`
(cosmetic, 0 warnings, left); no box, no ATAS, no money.

## 2026-09-12 — U-crlf-strategy-win landed: the day-one fixtures pinned to LF, and the test holds on either checkout

The windows-only red at `1e92fe3` (run 34719212649: the three theory cases of `DayOneStrategyTests.Each_fixture_is_the_program_
the_document_prints`, `Sub-string not found`), by one fresh fixer on `docs/briefs/U-crlf-strategy-win.md`. Merge `137aaa4`, 2
commits, test- and attributes-only: `git diff main -- src/` empty, no `Assert.` line in the diff; draft PR #18 for the runner, closed.

- **The shape, a second time:** `.gitattributes` pinned `*.md` (the document) to LF since `U-crlf-win` but said nothing about
  `*.strategy`, so `* text=auto` handed the runner's `core.autocrlf=true` checkout CRLF fixtures; the test normalised the
  document's line endings and read the fixture raw. A `git -c core.autocrlf=true clone` of `main` reproduces it: `w/crlf`, CR
  11/18/13 on the three files.
- **The class fix, both halves:** `*.strategy text eol=lf` in the source block, and `Fixture()` reads with `ReplaceLineEndings("\n")`
  so both comparisons hold on either checkout. RED on this Mac with the fixtures rewritten to CRLF: `Failed: 3, Passed: 7`, each
  case carrying the CI's own message; GREEN with the CRLF files still in place: `Passed: 10`; mutant (the normalisation removed
  again, CRLF in place): `Failed: 3, Passed: 7`. No `Timing` membership — no clock is involved.
- **Proven on the runner:** run 34720230379 at `e391ccd`, windows, ubuntu, macos and `package` all SUCCESS, the three cases
  `outcome="Passed"` in the windows trx; windows suites Unit 2 m 57 s, Fault 4 m 56 s, Integration 6 m 31 s.
- **Sweep of 89 file-reading sites in `tests/`:** 20 anchored at the repo root — 19 read a tracked `.cs` or `.md` (LF-pinned, most
  normalising anyway), the 20th is this fixture; the other 69 read files written at run time into temp directories. Nothing else
  at risk.

**Verified by running (the fixer, quoted; then the manager's gate):** fixer's gate at `e391ccd`, Release: 0 warnings, 0 errors, 17
projects; Unit 3× → 706/706 each; Unit 706 + Fault 277 + Integration 627 = 1610 passed, 0 failed, 1 skipped; names vs `main` → 0
removed, 0 added (two independent extractors). Manager's gate at `137aaa4`, Release: build → 0 warnings, 0 errors; suite → 706 + 277 + 627 = 1610 passed, 0 failed, 1 skipped;
names vs `main` → 0 removed, 0 added (sets 1309 = 1309); scan clean; no trailers; `rev-list --count` → 0; CI run 34721991796 at `137aaa4`: all four jobs GREEN — windows-latest with the three fixture cases, the red closed on `main`'s own runner.

**Carried forward:** a tracked file the tests read byte for byte needs BOTH its extension in `.gitattributes` and a test that
normalises what it reads — either alone has now failed once. **NOT done:** no product code; no assertion loosened; no box, no money.

## 2026-09-13 — U-runner-2 landed: the evaluator — closed bars of a named dataset in, bounded intents out, a fault a defined outcome

Rule 8's second half, by one fresh builder on `docs/briefs/U-runner-2.md`. Merge `c758fce`, 6 commits (new `Data/BarFeed.cs`,
`Strategy/StrategyIndicators.cs`, `StrategyEvaluator.cs`, `StrategyInterpreter.cs`, `StrategyCalendar.cs`, `EvaluationLimits`;
`DatasetStore.ById`; `DatasetReader.TryBar` extracted, its cap and body unchanged; `docs/STRATEGY-LANGUAGE.md` an "Evaluation"
section; `CONTRACTS.md`; seven new test classes). No schema change, nothing on the wire, no order.

- **Bars by dataset id:** `DatasetStore.ById` and a streaming `BarFeed` — a real twelve-month window (525,600 bars) is `OverCap`
  to the reader and streams whole through the feed; the `Checked` verdict taken ONCE at open (proven by deleting a raw file
  mid-run and still streaming). RED: `Value is null` / `the bar feed is not implemented`; mutant (`Checked` not called): 2 red.
- **Seven indicators**, 280 pinned values over a 40-bar fixture, every value recomputed OUTSIDE the build in 60-digit decimal:
  all 280 agree, worst relative deviation 2.3e-27 (the last digit `decimal` has). RED: `Expected: 101.50 / Actual: null`; mutant
  (EMA seeded from the first close): the series differs from bar 4.
- **Gaps and warm-up:** a missing minute advances no lookback and is counted; a bar out of order, repeated or off the grid is a
  defined fault. RED: `the evaluator is not implemented`; mutant (the previous close carried across the gap): `Expected: 99.60 /
  Actual: 99.30`.
- **The rule engine:** exits before entries, no same-event reversal, no duplicate entry while one is pending, a signal stamped with
  its bar and executable only after it; the calendar table checked outside the build against IANA — 525,888 half-hour readings
  across six zones and 2023–2027, zero mismatches. RED: `Assert.Single() Failure: The collection was empty` (22 red). **The
  brief's mutant corrected by the builder:** with the position read from the account input, "entries before exits" cannot reverse
  (an entry is gated on being flat); the guard that produces a one-event reversal is "an exit that fires ENDS the event", and that
  mutant went red: `Expected: Exit / Actual: Enter`, 3 of 28.
- **Limits and faults:** a per-event operation budget (16,384) and a state limit (262,144 bytes), both ABOVE what any accepted
  program can cost (8,401 / 138,464, pinned as an inequality); a fault is a value, never an exception. RED: `OverflowException`
  escaping the evaluator; mutant (the budget counted per rule): `Expected: 57 / Actual: 3`. The three day-one programs evaluate
  end to end with their intents pinned (crossover bar 60/142, RSI 30/59, breakout 61/154), recomputed outside the build.
- **Judged at landing:** four recorded departures — account state is evaluator INPUT, not vocabulary; a dataset carries no
  increment or quality flag, so quantities are unrounded and the contract says so; the deadline is the CALLER's token (no clock
  in the evaluator); the opening-range row of the language document corrected to "bars whose open time is inside the interval".
  Sizing (not an item) is in, because a bounded intent needs a quantity. `StrategyVersions` unmoved: the three golden ids stand.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `39c94e0`, Release: 0 warnings, 0 errors,
17 projects; 12 touched classes 3× → 106/106 (Unit) and 6/6 (Integration) each; Unit 783 + Fault 277 + Integration 627 = 1687
passed, 0 failed, 1 skipped; names vs `main` → 61 added, 0 removed; 127 test files all text. Manager's gate at `c758fce`,
Release: build → 0 warnings, 0 errors; suite → 783 + 277 + 627 = 1687 passed, 0 failed, 1 skipped; names vs `main` → 0 removed, 63 added; scan clean; no trailers; `rev-list
--count` → 0; CI run 34722641133 at `c758fce`: all four jobs GREEN — ubuntu, macos, windows-latest and `package`.

**NOT done:** no fills, fees, slippage, stop or target enforcement, capital, trace, persistence, report or pipe op (`U-runner-3`);
nothing reads `workspace/strategies/`; no box, no ATAS, no money.

## 2026-09-13 — U-containment landed: the agent process held in a job, given a clean environment, known by launch on the pipe, and refused in the armed live configuration

Rule 2 of `docs/COUNCIL.md` and the round-4 containment lines, by one fresh builder on `docs/briefs/U-containment.md` (dispatched
16:10, stalled twice by the Mac sleeping, resumed with its context both times; draft PR #17 for the windows runner). Merge
`7b90acf`, 6 commits, 33 files, +2605/−53 (new `Security/AgentGrants.cs`, `PeerImage.cs`,
`AgentRuntime/AgentEnvironment.cs`, `Containment.cs`; `AgentSession.cs`, `AgentSupervisor.cs`, `CliAgentRuntime.cs`, `TurnMeter.cs`,
`GatewayPipeServer.cs`, `GatewayTypes.cs` (`AgentContext.Role`/`AttemptId`), `PipeClient.cs`, `Protocol.cs`, `AppHost.cs`, the Doctor,
`CONTRACTS.md`, `COUNCIL.md`; eight test classes). No schema number: the grant register is in memory, the deployed-CLI hash a file.

- **A job that dies with the app:** on Windows every agent process is placed in a Job Object with `KILL_ON_JOB_CLOSE`, breakaway
  forbidden, assigned immediately after `Process.Start`; on macOS/Linux the child is its own session and the cancel kills it. Mac
  RED: "the turn's detached grandchild (pid 40448) was still running after CancelAsync"; **windows RED on the runner**, run
  34718510974: the same test, `Probe: ticks=10147, middle.done=True`; mutant (`SILENT_BREAKAWAY_OK`), run 34719666628: red again,
  "lets EVERY child start outside the job, asked for or not".
- **A clean environment:** a whitelist, never the inherited copy. RED: `TA_TEST_EXPORTED_CREDENTIAL … reached the agent process`;
  mutant (`Clear()` deleted): the same line.
- **The pipe knows which launch is calling:** a per-attempt grant (role, attempt id, expiry) handed only through the environment,
  required in `hello` beside the machine token, role and attempt stamped on `AgentContext`; the peer image checked as the bridge
  does it. RED: "a client holding nothing but the machine token … was served an order as though it were the Operations Director";
  mutant (`MayPlaceOrders => true`): "the Research Director's launch placed an order"; image mutant (the workspace clause dropped):
  a copied `trade.exe` inside the agent's tree refused by the path rule alone, the verdict no longer naming the workspace.
- **The protected configuration:** `Doctor` gains `Containment` (job, environment, grant, image, "OS sandbox: NONE" said plainly);
  while `ModeIsLive && LiveActivated` the vendor CLI is refused until an OS sandbox reports OK. RED: `Value is null` and "the vendor
  CLI ran in the armed live configuration"; mutant (`ModeIsLive` alone): `LIVE_CONFIRM` unactivated refused too.
- **Judged at landing, two deviations kept:** the child is assigned to the job right after start rather than started suspended
  (suspended needs raw `CreateProcess`, re-implementing the redirection the no-terminal rule rests on, to close a one-syscall
  race — a bound, recorded); a `hello` with no grant is served ROLELESS (may read, refused everything that moves money) rather than
  refused outright, so `trade status` on the box still answers while "the machine token alone is the chair" is closed. One extra
  commit: the Windows probe rewritten as script files with a heartbeat, because a quoted script through `cmd /c` never reached
  the runner's shell intact — the first two windows runs failed on the probe, not the product.
**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `3735a17`, Release: 0 warnings, 0 errors;
touched classes 3× → 30/30, 61 + 1 skipped, 34/34 each; Unit 736 + Fault 277 + Integration 635 = 1648 passed, 0 failed, 1 skipped;
names → 39 added, 0 removed; CI 34720169201 and 34721448060: ubuntu and macos green, windows red only on `main`'s own CRLF red
(this unit's Fault 272/272, Integration 547/548 there). Manager's gate at `43323f3` (rebased over the CRLF fix and `U-runner-2`; landed as `7b90acf`
after a docs-only rebase, `src` and `tests` identical), Release: build → 0 warnings, 0 errors; suite → 813 + 277 + 635 = 1725
passed, 0 failed, 1 skipped; names vs `main` → 0 removed, 34 added (sets 1309 → 1343); scan clean (grant `Token`
fields, judged); no trailers; PR #17's runners at `ee2abb2`: run 34722253017, all jobs GREEN, PR closed; `rev-list --count` → 0; CI run 34723265426 at `7b90acf`: all four jobs GREEN — ubuntu, macos, windows-latest and `package`.

**NOT done:** no OS sandbox — same-user reads and writes of `state/` stand, a Unix session is escapable by its own `setsid`
(`U-contain-2`); the peer-image kernel call is Windows-only, the Doctor row saying the rule is unenforced elsewhere; no box.

## 2026-09-13 — U-runner-3 landed: the backtest — a declared execution model on bars, a trace the app scores, a persisted version and run, the request under the launch's own identity

Rule 8's third part, by one fresh builder on `docs/briefs/U-runner-3.md` (killed by the session limit at 00:53 with all five items
committed, RESUMED from its transcript at 13:05 with its context, then told of one gap a read-only survey found on its branch). Merge
`5020b2b`, 7 commits, 24 files, +4219/−8 (new `Core/Strategy/Backtest.cs`, `Db/StrategyStore.cs`, `Gateway/Backtests.cs`; `Database.cs`
**schema 12** — `strategy_version`, `strategy_run`, `strategy_trade`; the `backtest` pipe op and `trade backtest`; `CONTRACTS.md`,
`STRATEGY-LANGUAGE.md`, the guide; six test classes).

- **Three app-owned tables**, a version keyed by the program's own hash, a run keyed by every input (version, dataset id and its sha at run
  time, window, execution model), `ON CONFLICT DO NOTHING`, no pipe op writes them. RED: a program is only a file; mutant (the version id
  minted from the attempt): every restart a new version.
- **The declared execution model:** a signal fills at the next bar's open plus adverse slippage, a fee per fill, sizing rounded DOWN to the
  run's declared increment (a dataset carries none), stops, targets and maximum holding bars checked on every later bar with conservative
  ordering (a bar touching both counts the stop). RED: a fill at the signal bar's own close; mutant (target before stop): the fixture's pnl.
- **The trace and the metrics from it alone** — trades, win rate, net after fees, drawdown on equity including the open trade, exposure,
  gap and fault counts; every unknown a labelled dash. RED: drawdown from closed trades; mutant (a metric read from the program): red.
- **Deterministic and refusable:** no clock inside a run, a byte-identical trace and the same run id for the same inputs, a changed fee a
  different id, a `REJECTED` dataset refused. RED: two runs of one input differ; mutant (the dataset sha left out of the id): red.
- **The request and the report:** `trade backtest --strategy <file> --dataset <id> …`, a read-only op (not in `Ops.Mutating`), one run at a
  time per role; section 8 lists runs "measured by TradeAgent" and its gap line goes only when a run exists. RED: the gap line stands with a
  run; mutant (an agent's claimed metric listed as measured): red.
- **The launch-identity gap, found by the survey and closed inside the unit** (`bad4f74`): the op took no `AgentContext` and resolved a
  relative path against the chair's home first, so a Research request could read the chair's copy and be recorded as the chair's run.
  Now the role and attempt come off the launch grant, the program resolves inside `Paths.RoleHome(ctx.Role)` only — lexically and with every
  symlink on the way resolved — and a caller with no council role is refused. RED: `Expected: "research" / Actual: "operations"`; mutant
  (the role from the folder with a grant present): the same. A malformed path is refused in words instead of `UNKNOWN_ERROR`.
- **Two bounds the builder introduced, called out:** `Backtest.MaxTracedBars = 200_000` (a twelve-month backtest is four requests, not one)
  and `ExecutionModel` refusing a fee or slippage above 0.05.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `bad4f74`, Release: 0 warnings, 0 errors, 17
projects; touched classes 3× → Unit 65/65, Integration 101 + 1 skipped, backpressure 34/34 each; Unit 854 + Fault 277 + Integration 645 =
1776 passed, 0 failed, 1 skipped; names → 47 added, 0 removed; 127 test files text. Manager's gate at `5020b2b`, Release: build →
0 warnings, 0 errors; suite → 854 + 277 + 645 = 1776 passed, 0 failed, 1 skipped; names vs `main` → 0 removed, 47 added (sets 1406 → 1453); scan clean (`ipc.token` in a traversal-refusal
test, judged); no trailers; `rev-list --count` → 0; CI run 34756421303 at `5020b2b`: all four jobs GREEN — ubuntu, macos, windows-latest and `package`.

**NOT VERIFIED:** the CLI verb inside the suite — `Map` is a private local function of the top-level `Program`, so `trade backtest` was
proven only by hand (`IPC_UNAVAILABLE` with no app, `unknown command` on a typo, the help lines). **NOT done:** no live or paper execution,
no protection between evaluations (`U-flatten`), no promotion or referee (`U-referee-1`, `-2`), no box, no ATAS, no money.

## 2026-09-13 — U-api-worker landed: the app-owned harness — one provider, one role on it, every tool a grant, every boundary counted, the key in memory only

"Workers run on an app-owned harness" (`docs/COUNCIL.md`), by one fresh builder on `docs/briefs/U-api-worker.md` (killed by the session
limit at 00:53 mid-item-4 with items 1–3 committed and item 4 half-edited on disk, RESUMED from its transcript at 13:05, rebased four
times as `main` moved — three real conflicts resolved inside the rebase, the schema ladder carrying 11 → 12 → 13 in order). Merge
`ff8b43c`, 7 commits, 34 files, +4414/−50 (new `ApiAgentRuntime`, `ApiConversation`, `GrantedWorkerTools`, `Security/HarnessKey.cs`,
`FakeProvider`; `Database.cs` **schema 13** — `tool_call`; `TurnMeter.cs`, `AgentSupervisor.cs`, `AppHost.cs`, `DashboardView.cs`,
`DailyReports.cs`, `GatewayPipeServer.cs`, `ListPrices.cs`, `CONTRACTS.md`, the guide; seven test classes).

- **The runtime `openai-api`** from manifest data (endpoint, `max_completion_tokens`, the model `ListPrices` prices), the provider's tool
  loop run BY THE APP, usage summed over every response. RED: `Collection was empty`, `costs.json has no price for …`; mutant (usage from
  the last response only): `Expected: 6000 / Actual: 3000` — a three-request turn priced as one.
- **Tools are grants, default deny:** six tools; `read_file`/`list_files` inside the role's home and `in/`, `write_file` into `out/` and
  the role's `trading/`, `trade` through the pipe server's own call path so the role check is the pipe's one line, `data`, `report`; every
  call a `tool_call` row. RED: `Expected: 13 / Actual: 11`; mutant (the role check dropped): Research's `trade buy` served; path mutant:
  `read_file("../agent/trading/PLAN.md")` served.
- **Every boundary counted before each request:** the reservation first, then input, output and retrieval bytes against `TurnAllowance`,
  over it `CONTEXT_BUDGET_EXCEEDED` with staged files kept. RED: `Expected: 3 / Actual: 24` (and 2, 8); mutant (`>=` → `>`): a fourth
  request past the allowance — the brief's "check after the request" mutant is behaviourally identical inside a turn, stated.
- **One role on it:** per-role runtime and model on the Safety page, Research defaults to the harness once a key is held, the chair stays
  on codex; the key in a masked box, in memory only, cleared on dispose; "harness key: held / not held" on the daily report. RED:
  `Expected: "openai-api" / Actual: "codex"`; mutant (the key check dropped): a role with no key sent a request.
- **Never the network:** `FakeProvider` on loopback (a HEAD answered with headers only, every response closed in a `finally`); the
  vendor-reach scan extended to the provider's host and matching the TYPE — it caught a live offender in the unit's own tests first.
- **Judged at landing:** `backtest` is deliberately NOT on the harness's closed `trade` op list (a decision, briefed as `U-harness-loop`);
  `Containment.RefusalToLaunch` does not apply to the harness (no child process); `TurnMeter` prices by (runtime, model).

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at the tip before a docs-only rebase, Release: 0
warnings, 0 errors (the build re-run at the tip); touched classes 3× → 201/201 and 103 + 1 skipped each; Unit 920 + Fault 277 +
Integration 645 = 1842 passed, 0 failed, 1 skipped; names → 47 added, 0 removed; every test source text. Manager's gate at `98d23a2` (landed as `ff8b43c` after a docs-only rebase, `src` and `tests` identical),
Release: build → 0 warnings, 0 errors; suite → 920 + 277 + 645 = 1842 passed, 0 failed, 1 skipped; names vs `main` → 0 removed, 52 added (sets 1453 → 1505); scan clean (test names and a
placeholder token string, judged; no key-shaped literal); no trailers; `rev-list --count` → 0; CI run 34758232365 at `ff8b43c`: all four jobs GREEN — ubuntu, macos, windows-latest and `package`.

**NOT VERIFIED:** the mission loop over a harness conversation — `ConversationFor(role)` is wired and read by the loop, but every harness
turn in the suite is driven by calling the conversation directly (`U-harness-loop`). **NOT done:** no real provider call has ever been
made by this repository (the manifest stays `Verified = false`); the chair is not on the harness; no memory search, no grant table with
fencing, no second provider, no key on disk; no box, no ATAS, no money.

## 2026-09-13 — U-referee-1 landed: the protocol — a holdout no pipe caller can reach, a campaign with its policy fixed, trials charged, verdicts scarce

Rule 9's protocol half (`docs/COUNCIL.md:130-136`), by one fresh builder on `docs/briefs/U-referee-1.md`, written from a read-only survey.
Merge `19ef096`, 8 commits, 26 files, +3090/−56 (`Database.cs` **schema 14** — `holdout_from` and `evaluation_class` on `dataset`,
`strategy_campaign`, `strategy_trial`, `strategy_verdict`; `DatasetStore.cs`, `DatasetReader.cs` and `BarFeed.cs` requiring a `BarAudience`;
`CampaignStore`, `Referee`, `HoldoutFeed`; `GatewayPipeServer.cs`, `Backtests.cs`, the Data page card, `Errors.cs`, `CONTRACTS.md`, the guide;
six test classes). Rebased three times as `main` moved, the schema ladder reconciled 11 → 12 → 13 → 14 with no edit to either neighbour.

- **The holdout is a cutoff the owner sets and nothing on the pipe can set, clear or move:** `holdout_from` (UTC, INCLUSIVE) and the class,
  one writer, a two-press card in BOTH directions because there is no way back; moving a cutoff earlier is refused. RED: `no such column:
  holdout_from`; mutant (`data-bars` honouring a cutoff from the request): `Expected: 2026-08-01T01:00:00 / Actual: …01:30:00`.
- **No caller on the pipe reads a holdout bar, and the refusal is the DEFAULT path:** both readers REQUIRE a `BarAudience`, a forgotten
  refusal yields an empty window, the only audience that may pass a cutoff is `internal` to Core and a reflection test holds it to one
  public door; a window is served only when its end is PROVED before the cutoff, so an unbounded `to` is refused rather than clipped;
  every op is swept off `Ops`' own fields. RED over the wire: `a caller on the agent pipe was served the bars the owner held back`, `a
  backtest ran over the months the owner held back`; mutant (a null role read as "not research"): the roleless caller served.
- **`strategy_campaign`**, policy text and sha fixed at open, one open campaign per holdout dataset (a partial unique index, proved by
  writing past the store), renewal by code carrying `renewed_from` AND the parent's policy. RED: `no such table`; mutant (a renewal with
  no `renewed_from`): `Expected: 1 / Actual: null`.
- **`strategy_trial`**, one per registered research run, keyed by campaign + version + run and by nothing about who asked (a test holds
  the column list); a `fixture` dataset's run charged nothing. RED: `Expected: 1 / Actual: 0`; mutant (keyed by the attempt): a restart
  buys two more trials, `Expected: 1 / Actual: 3`.
- **The verdict budget, charged over the renewal LINEAGE before a holdout bar is read** (a per-campaign count would hand access back on
  renewal, `COUNCIL.md:132`). RED: `a second verdict was authorised on a budget of one`; mutant (charged after the run): the row absent
  when the request answered. Defaults `CampaignTrialBudget` 200, `CampaignVerdictBudget` 3, copied onto the campaign at open.
- **Judged at landing:** `U-api-worker`'s schema pin turned into a floor (the same move its own report made to four others); the choices
  where COUNCIL is silent are in `CONTRACTS.md` as choices.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `66335c1`, Release: 0 warnings, 0 errors (DLL
timestamps checked); touched classes 3× → 97/97 (Unit) and 27/27 (Integration) each; Unit 959 + Fault 277 + Integration 656 = 1892 passed,
0 failed, 1 skipped; names → 38 added, 0 removed; 145 test files text. Manager's gate at `19ef096`, Release: build → 0 warnings, 0 errors; suite →
959 + 277 + 656 = 1892 passed, 0 failed, 1 skipped; names vs `main` → 0 removed, 38 added (sets 1505 → 1543); scan clean (grant tokens in tests, judged); no trailers; `rev-list
--count` → 0; CI run 34767356515 at `19ef096`: all four jobs GREEN — ubuntu, macos, windows-latest and `package`.

**NOT done:** the verdict itself (`U-referee-2`); no owner-visible budget line, nothing on the daily report, no renew button; no clear for a
holdout and no way down for a class (both would un-hold bars); a dataset with no holdout has no campaign, so its runs are charged nothing;
the trial budget can be exceeded by ONE under two concurrent roles (the charge registers with the run, the refusal is checked before it —
`U-council-concurrent`); no box, no ATAS, no money.

## 2026-09-13 — U-harness-loop landed: the mission loop proven over a harness turn end to end, a cut turn tells the next one why, the backtest granted to a harness worker

The gap `U-api-worker` named, by one fresh builder on `docs/briefs/U-harness-loop.md`. Merge `47d1071`, 4 commits, 8 files, +761/−10
(`MissionLoop.cs`, `ApiConversation.cs`, `GrantedWorkerTools.cs`, `CONTRACTS.md`, the guide; `HarnessLoopTests`, `HarnessBudgetTests`).
No schema change.

- **A loop turn on the harness, end to end:** `MissionLoop.TurnAsync` over a real `ApiConversation` against the loopback provider —
  admitted through `TurnMeter.Begin`, launched through `ConversationFor(role)`, the canned calls read the file the Situation NAMED and
  write `out/report-<attempt>.md`, the transition ends the launch at the model's exact price (6,000 in / 90 out), publishes under that
  attempt, raises Operations' `task:` event and delivers the file. **The product was already right**, so the test is the deliverable and
  its RED is the mutant (`ConversationFor(role)` → the chair's conversation): `Expected: 3 / Actual: 0` — the provider saw no request.
- **A turn cut mid-loop is committed like any other — two clauses right, one wrong:** a cut turn already ended ENDED with
  `CONTEXT_BUDGET_EXCEEDED` and already published its staged report on the same commit, but nothing told the next turn. RED: `Not found:
  "TradeAgent stopped your last turn"`; fixed — `AgentTurnEnded.Cut` carries the bound's own sentence out, `MissionSituation.Cut` renders
  it above `Restored`, held PER ROLE until that role's next turn; mutant (`Commit` returning early for a failed turn): `Expected: ENDED /
  Actual: LAUNCHED`.
- **`backtest` granted to a harness worker:** a real run through `GrantedWorkerTools` into the gateway's own handler, on a program in
  Research's home, the run row carrying that role and attempt; `backtest` stays out of `Ops.Mutating`. RED: `'backtest' is not an
  operation 'trade' carries`; mutant (added to `Ops.Mutating` instead): `ROLE_MAY_NOT_TRADE`.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `ec44e40`, Release: 0 warnings, 0 errors, 17
projects; seven touched classes 3× → 89/89 each; Unit 923 + Fault 277 + Integration 645 = 1845 passed, 0 failed, 1 skipped; names → 3
added, 0 removed. Manager's gate at `118e7fb` (rebased over `U-referee-1`; landed as `47d1071` after a docs-only rebase, `src` and `tests` identical), Release: build → 0 warnings, 0 errors; suite → 962 + 277 + 656 = 1895 passed, 0 failed, 1 skipped; names vs
`main` → 0 removed, 3 added; scan clean (token counts in assertions, judged); no trailers; `rev-list --count` → 0; CI run 34768117002 at `47d1071`: all four jobs GREEN — ubuntu, macos, windows-latest and `package`.

**NOT done:** no real provider call (every request went to the loopback fake); the one-run-at-a-time guard on the worker route is pinned
by `BacktestRequestTests` rather than re-proved here; `MissionSituation.Cut` is in memory and does not survive a restart (`ai_attempt.
context` is the durable copy); the chair is not on the harness; no box, no ATAS, no money.

## 2026-09-13 — U-referee-2 landed: the verdict — a promotion record bound by hash, invalidated by a changed assumption, forward evidence after the freeze, delivered without a figure

Rule 9's verdict half, by one fresh builder on `docs/briefs/U-referee-2.md` (killed by the session limit at 15:35 before reading anything,
resumed from its prompt at 18:05). Merge `45719b2`, 8 commits, 15 files, +1920/−14 (`Database.cs` **schema 15** — `strategy_promotion`;
new `PromotionStore`, `Referee.Verdict`, `ScoringPolicyV1`; `PublicationStore.cs` (an optional event id on `Commit`), `MissionEventStore.cs`
(`verdict`), `MissionLoop.cs` (the Situation's promoted line), `DailyReports.cs`, `CONTRACTS.md`; three test classes). Nothing on the order path.

- **One immutable row addressed by the nine facts it binds** (version, campaign, policy sha, interpreter build, holdout dataset and its sha at
  run time, execution model, evaluator version, holdout run), `ON CONFLICT DO NOTHING`, no update, no delete — a reflection test holds the
  store to `Record/ById/For/All/Standing`. RED: `no such table: strategy_promotion`; mutant (the clock hashed into the id): two ids for one
  judgement.
- **The holdout run is the referee's own, through the one in-process door, and is not a research trial.** RED (no door): "the holdout of
  campaign 1 could not be run … holds out every bar from …"; mutant (registered as a trial): `Expected: 1 / Actual: 2` — the evaluation
  charged twice.
- **A changed assumption invalidates the evidence, computed at read time, never written back:** a `REJECTED` dataset, a changed interpreter
  build or policy sha → `invalidated` with the reason. RED: `Expected: "invalidated" / Actual: "promoted"` on all three; mutant (the dataset's
  CURRENT sha compared with itself): the re-collected case alone.
- **Forward evidence after the freeze:** the holdout window must begin after the version's `created_at`. RED: "a version frozen after the
  holdout begins promoted on it" (and the run in that test is the profitable one, so it is the date that refuses); mutant (compared against
  when the run was made): refuses nothing.
- **Delivered and told, with no figure:** one `verdict` wake for Research keyed by the promotion, a note whose only digits are the campaign
  id (asserted digit by digit), the Situation naming the promoted version, section 8 listing every promotion, refusal and invalidation.
  RED: `Not found: "PROMOTED version …"`; mutant (the holdout's net appended to the note): `Expected: "1" / Actual: "148"`.
- **Found while wiring section 8:** `trade report` hands the agent the WHOLE rendered report, so a valued holdout line would have leaked
  through it what `data-bars` and `backtest` refuse. The holdout run is named, dated and counted, never valued — proved over the wire on a
  connection refused the same bars a moment later.
- **Choices where COUNCIL is silent, in `CONTRACTS.md` as choices:** a separate promotion table (the verdict row is the CHARGE, this the
  ANSWER); the scoring policy is CODE bound to the campaign's text by sha, a campaign fixing another policy is not judged; its clauses are
  forward evidence, completed, one closed trade, net above declared costs — no drawdown or sample floor, because nobody measured one; the
  execution model is the JUDGE'S (default `Frictionless`, hashed in); `Standing` reads the ledger state, not the file, for the money path.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `3f075be`, Release: 0 warnings, 0 errors (DLL
timestamps checked); touched classes 3× → 18/18 and 1/1 each; Unit 980 + Fault 277 + Integration 657 = 1914 passed, 0 failed, 1 skipped;
names → 19 added, 0 removed; 140 test files text. Manager's gate at `45719b2`, Release: build → 0 warnings, 0 errors; suite → 980 + 277 + 657 = 1914 passed, 0 failed, 1 skipped; names vs
`main` → 0 removed, 19 added (sets 1546 → 1565); scan clean; no trailers; `TradingGateway.cs` diff read at landing — 22 added lines, the `Referee` and `Promotions` properties outside the handler table, the gate chain untouched; `rev-list
--count` → 0; CI run 34771155930 at `45719b2`: ubuntu and macos GREEN; **windows-latest RED** on ONE Fault test, `CancelAllAgainstOpenWorkTests.An_agent_modify_inside_the_connector_call_does_not_survive_the_cancel_all_press` (`PressInFlightTests.cs`): the press waited on the open work and then had no budget left for its own cancel leg — `1 of 1 record(s) from this press are still waiting for you`, the runner's disk spending the two-second budget, the class fixed in the fixture for two sibling classes already (`U-press-win-3`, `U-press-settle-win`); this unit touched nothing on the order path; fixer `U-press-inflight-win` on top; recorded red until it lands.

**NOT done:** no way for the owner to ASK for a verdict — `Referee.Verdict` runs from code and tests only, no card, no op, no verb (a test asks
the whole op vocabulary by name); nothing reads `Promotions.Standing` on the order path yet (`U-freshness`/`U-flatten`); the trial-budget race
untouched (`U-council-concurrent-2`); the guide says nothing about verdicts; no box, no ATAS, no money.

## 2026-09-13 — U-council-concurrent-1 landed: two roles' turns may overlap — every single-slot assumption the serial council allowed is per role

The leases `docs/COUNCIL.md:259-260` deferred, by one fresh builder on `docs/briefs/U-council-concurrent-1.md` written from a read-only
survey of what each landed unit had left single-owner. Merge `4753c46`, 7 commits, 15 files, +1288/−142 (`MissionLoop.cs`, `TurnMeter.cs`,
`AiAttemptStore.cs`, `CouncilRelay.cs`, `AgentPresence.cs`, `MaterialScanner.cs`, `AppHost.cs`, `CONTRACTS.md`; eight tests). No schema change:
the lease is in memory, as `CONTRACTS.md` already chooses for the app's two other leases, and the LAUNCHED `ai_attempt` row is its durable
witness, turned LOST at its reservation by the next start.

- **A per-role turn lease** (`_turning`), taken immediately before `BeginTurn`, dropped beside `Commit` and on every other way out; a
  second `TurnAsync` for that role refused in words, never queued; a turning role stepped over when the next role is chosen. `_working` is
  gone — "working" on the card IS the lease. RED (two real threads through a `Barrier(2)`): `Expected: 1 / Actual: 2` launches for one
  role; mutant (the lease taken after `BeginTurn`): the same.
- **`_staged` per role, keyed by the attempt it closes;** `CommitStaged(role)` and `Begin`'s drain touch only their own. RED: `Expected:
  ENDED / Actual: LAUNCHED` on the chair's row; mutant (every held close written): Research's close inside the chair's transaction.
- **`LiveAttempts`, the register of launches this process is flying:** a second meter over the same file loses every other open row and
  skips the live one — skipping releases no money, a LAUNCHED reservation counts like a LOST cost. RED: `Expected: LAUNCHED / Actual:
  LOST`; mutant (the skip narrowed by role): Research's live turn lost.
- **The relay pass is the turning role's own;** `Reconcile()` is the start-up pass and the only one over every role; the fence LEAVES a file
  whose launch this process is flying rather than quarantining it; the staged files are read BEFORE the turn's `Database.Write` opens.
  REDs: `Collection: ["report-turn-b.md"]` quarantined, and "the other role's launch record was blocked behind this role's file read"
  (the stack inside `Database.Write`); mutant (any LAUNCHED row publishable): a stale process's file published as live work.
- **Quiescence is of every managed agent** (rule 7), in both directions — no pass while a role turns, no launch while a pass runs; the
  session count, the error run and the cap notice per role. REDs: `Expected: Inbox / Actual: InboxUnattested`, and `Expected: 0 /
  Actual: 1` for Research's session rotated by the chair's turns; mutants: the same attestation red, and `Expected: 00:02:00 / Actual:
  00:00:30` with one error slot. `CouncilLoopTests` now MEASURES `Peak == 2` with the card reading both directors working; the old
  `Peak == 1` kept where it is true; `AppHost.cs`'s "safe because the council is serial" rewritten.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `49c7135`, Release: 0 warnings, 0 errors, 17
projects; nine touched classes 3× → 102/102 each; Unit 988 + Fault 277 + Integration 657 = 1922 passed, 0 failed, 1 skipped; names → 8
added, 0 removed; every test source text; rebased three times with no conflict. Manager's gate at `4753c46`, Release: build →
0 warnings, 0 errors; suite → 988 + 277 + 657 = 1922 passed, 0 failed, 1 skipped; names vs `main` → 0 removed, 8 added (sets 1565 → 1573); scan clean (usage field names, judged); no trailers;
no gateway or protocol file touched; `rev-list --count` → 0; CI run 34773015349 at `4753c46`: all four jobs GREEN — ubuntu, macos, windows-latest (the press fixture red of `45719b2` not reproduced there) and `package`.

**NOT done, the bound stated:** `LoopAsync` still drives one turn at a time — the guards make an overlap SAFE and two callers prove it,
but a second driver is not this unit; `WorkspaceRevisions.Snapshot` still reads its two size-capped files inside the transaction (only the
relay's unbounded `out/` read moved out); one existing test's setup changed, not its assertion (each simulated "process" its own register);
the boundary, assessments, dispositions and the trial-budget race (`U-council-concurrent-2`); a third role; no box, no money.

## 2026-09-13 — U-council-concurrent-2 landed: the consequential boundary — two sealed assessments, one bounded challenge, one disposition applied by code

`docs/COUNCIL.md:59-65`, the one paragraph of the doctrine that had no product code, by one fresh builder on
`docs/briefs/U-council-concurrent-2.md`. Merge `cac1787`, 6 commits, 22 files, +2034/−44 (`Database.cs` **schema 16** — `boundary_event`,
`boundary_submission`; new `Db/BoundaryStore.cs`; `CouncilRelay.cs` (`CommitSealed`, `Land`), `PublicationStore.cs`, `MissionEventStore.cs`,
`MissionLoop.cs`, `CampaignStore.cs`, `DailyReports.cs`, `DailyReport.cs` (section 9), both directors' guides, `CONTRACTS.md`; six test
classes). Nothing on the order path; no pipe op or verb reaches any of it (a test asks the shipped `Ops` vocabulary by name).

- **A boundary event keyed (kind, entity, revision),** opened by the referee in the verdict's own transaction, waking both directors once
  with one paid turn each; a repeat writes nothing (a UNIQUE index, proved by writing past the store). RED: `no such table: boundary_event`;
  mutant (the key from the attempt): `Expected: 2 / Actual: 4` paid wakes after a restart.
- **The sealed pair:** each assessment committed at once, its `delivery` WITHHELD (a delivery STATE, not a missing row, so a restart can
  tell held from failed) until both exist, released together by `Deliver`; a second from one director refused by words and by the primary
  key. RED (`Commit` in place of `CommitSealed`): the first assessment in the chair's `in/` before it wrote its own; mutant (released when
  one exists): the same.
- **One bounded challenge per boundary,** from either director after both assessments are delivered, buying the peer one turn; a second
  refused and unpaid. RED: two `challenge` wakes; mutant (the cap dropped): the over-length challenge's wake.
- **The disposition written by CODE** at the deadline or when the challenge window closes (both delivered AND the one challenge written),
  from the policy's default — `Promotions.Standing` for a promotion boundary; the window a 24-hour CONSTANT, not a setting, because a
  deadline the owner could move is the veto `:62` forbids; the loop sleeps until the earliest deadline and launches nothing to settle one;
  section 9 lists every boundary with its evidence, owner and deadline, OVERDUE past it. RED: `Expected: "deploy" / Actual: null`; mutant
  (`now <= DeadlineAt`): disposed a minute after opening, before either assessment.
- **The trial charged in ONE `Database.Write`** (the by-one race `U-referee-1` named). RED (two real threads on the last of 200):
  `Expected: 200 / Actual: 201`; mutant (the already-registered return dropped): a re-asked trial refused in words. **NOT VERIFIED:** the
  brief's own mutant (the count read outside the write) did not go red, because the test's barrier synchronises the pre-run look, not the
  store's read; the substitute mutant above is the one quoted, and a deterministic version would need a second seam inside `RegisterTrial`.
- **Choices where COUNCIL is silent, in `CONTRACTS.md`:** a promotion boundary is (version, campaign); opening buys the two turns and
  releasing buys none; which boundary a submission answers is the app's (the oldest open one that role has not answered); a trial
  refused after its run rolls the run back and its figures are never served. Items 2 and 3 share one commit (one protocol, one file).

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `0937b43`, Release: 0 warnings, 0 errors (DLL
timestamps checked); six touched classes 3× → 81/81 each; Unit 1008 + Fault 277 + Integration 657 = 1942 passed, 0 failed, 1 skipped —
with two other legs' suites running on this Mac at the same time; names → 20 added, 0 removed; 159 test sources text. Manager's gate at
`cac1787`, Release: build → 0 warnings, 0 errors; suite → 1008 + 277 + 657 = 1942 passed, 0 failed, 1 skipped; names vs `main` → 0 removed, 20 added (sets 1573 → 1593); scan clean; no
trailers; no order-path file touched; `rev-list --count` → 0; CI run 34840620999 at `cac1787`: all four jobs GREEN — ubuntu, macos, windows-latest and `package`.

**NOT done:** nothing reads a disposition — a `deploy` deploys nothing, the order path is untouched (`U-allocator-1`); the allocator,
evolution and retirement absent beyond `BoundaryKind.Retirement` as a value nothing opens (`U-allocator-2`); no boundary in the app's own
window, no way for the owner to open, extend or answer one; no box, no ATAS, no money.

## 2026-09-14 — U-press-inflight-win landed: the press family's fixtures take the generous budget, thirty of them, judged first

The windows-only red at `45719b2` (run 34771155930: `CancelAllAgainstOpenWorkTests.An_agent_modify_inside_the_connector_call_does_not_
survive_the_cancel_all_press`, the press's own write-ahead row left `UNKNOWN` because its order read was refused at the two-second
deadline), by one fresh fixer on `docs/briefs/U-press-inflight-win.md` (killed by the session limit while waiting for its runners,
resumed the next day). Merge `30dc8ed`, 3 commits, test-only: `git diff main -- src/` empty, no assertion changed (seven files diffed
line by line); draft PR #19 for the runner, closed after.

- **Judged, not assumed:** the red fixture's budget cut on this Mac — 100, 50, 20, 10, 5 ms all PASS (this disk carries the press from
  its deadline to the wire in under 5 ms); at 1 ms it fails with the CI's own assertion and all five of its output lines byte for byte; a
  temporary print named the single row, the press's write-ahead with no leg behind it. The product refused honestly: no product change.
- **The fixture, once:** all five presses in `PressInFlightTests` take `PressBudget` (20 s) through a new `emergencyBudget:` on
  `Stranded.Ready` and `SlowRead.Ready`; none of them has the two-second promise as its verdict, so none stays on two seconds and no
  `Timing` trait was added. Mutant (the generous budget removed with the 1 ms injection in place): RED in 5 of 5 runs.
- **The sweep, thirty fixtures moved:** the eleven `DispatchRecoveryTests` presses named EXPOSED on 2026-09-12 (every verdict a record, a
  state or a book, the EXPOSED paragraph rewritten); ten in `EmergencyPressTests` (its three deadline tests keep two seconds, their verdict
  IS the promise, the `Timing` trait untouched); all four of `PressAtomicityTests`. `AgentCloseAtDispatchTests` and `CompositeOwnerTests`
  NOT at risk, structurally: `RiskReducingScope.Begin` exists at three places in `src/`, none on their path.
- **A FOURTH fixture of the class found on the PR's first windows attempt:** `SweepRequestIdTests.Every_sent_not_confirmed_leg_carries_an_
  unknown_record_that_will_be_reconciled` (`Assert.NotEmpty() Failure`, green on the re-run) — it takes a 5 s budget and arranges a 2000 ms
  cancel to be the call that runs out, so a disk that spends the budget first turns `sent-not-confirmed` into `not-sent`; a paired
  budget-and-latency change, briefed as `U-sweep-latency-win`, not done here.

**Verified by running (the fixer, quoted; then the manager's gate):** fixer's gate at `0a531b5`, Release: 17 projects, 0 warnings, 0 errors;
Fault 3× → 277/277 each; Unit 1008 + Fault 277 + Integration 657 = 1942 passed, 0 failed, 1 skipped; names vs `main` → 1592 = 1592.
Runner: PR #19, run 34773625675 at `62dd169`, SUCCESS on all four jobs — windows Fault 272/272 in 9 m 34 s against 271/272 at the red sha;
macos's `Timing` step took its one second attempt (87/88 then 88/88). Manager's gate at `53690f5 (landed as `30dc8ed` after a docs-only rebase, `tests` identical)`, Release: build → 0 warnings, 0 errors; suite →
1008 + 277 + 657 = 1942 passed, 0 failed, 1 skipped; names vs `main` → 0 removed, 0 added (sets 1593 = 1593); scan clean; no trailers; `rev-list --count` → 0; CI run 34843618128 at `30dc8ed`: all four jobs GREEN — windows-latest with the moved fixtures, the red of `45719b2` closed on `main`'s own runner.

**NOT done:** no product code; `SweepRequestIdTests` untouched (above); the pushed tip had no runner run of its own — the proof is the run
at `62dd169`, the difference two council landings touching no Fault fixture plus the report; no box, no ATAS, no money.

## 2026-09-14 — U-venue-catalog landed: the instrument as a recorded fact with a source, not a number an agent typed into a request

The first of the venue and data units (COUNCIL names none there; `:145,152` require an increment on the bars), by one fresh builder on
`docs/briefs/U-venue-catalog.md` written from a read-only survey (killed by the session limit before its full suite, resumed the next day
and rebased three times, the schema ladder reconciled 15 → 16 → 17 inside the rebase). Merge `afa49dd`, 7 commits, 31 files, +2069/−58
(`Database.cs` **schema 17** — `venue`, `venue_instrument`, `dataset.venue_id`/`instrument_symbol`, `strategy_run.increment_source`; new
`Db/VenueStore.cs`, `Data/VenueCatalog.cs`; `Backtests.cs`, `GatewayPipeServer.cs` (`venue-list`), `GatewaySchema.cs`, `Protocol.cs`, the
CLI, `GrantedWorkerTools.cs`, `DailyReports.cs`, `CONTRACTS.md`, the guide; seven test classes). Not the money path: nothing near `PlaceAsync`.

- **`venue` and `venue_instrument` as app-owned data,** built-ins (the simulator's four futures, verified because the venue is this app's
  own; Binance spot's BTCUSDT, `verified = false` because nothing in this build has read Binance's own definition) overridable by
  `venues.json`, every row with `source`, `recorded_at`, `verified`; an unreadable file empties the table and says why. RED: `no such
  table: venue_instrument`; mutant (every row read back as verified): "nothing in this build has confirmed Binance's own instrument
  definition".
- **`venue-list` is a READ,** not in `Ops.Mutating`, on the harness worker's `trade` list for every role, `trade venue list`. RED: `the schema
  does not name 'venue-list'`; mutant (on the mutating list): `ROLE_MAY_NOT_TRADE` for the one role whose job is to size positions.
- **A dataset names its venue and instrument,** backfilled, carried by `data-list` and section 8; the backfill proved by taking a real
  database back to 16 and reopening it. RED: `data-list carries no venue for a recorded dataset`; mutant (derived from the pair instead of
  the recorded column): `Expected: "sim-futures" / Actual: "binance-spot"`.
- **A run's increment comes from the catalogue when the request omits it,** by the DATASET's instrument, never the program's line; a
  declared one wins; provenance recorded (`increment_source`) but not hashed, so no run id moves. RED: `Expected: 0.00001 / Actual: 1`;
  mutant (provenance folded into `Canonical`): `BacktestDeterminismTests` red — every run id moves.
- **A refusal, never a guess:** an unknown instrument, an unverified row, or a dataset naming no instrument is refused in words naming
  both routes out. RED: `No exception was thrown` — an unknown symbol ran silently at 1; mutant (the refusal returning 1): the same.
- **Found by the gate:** the catalogue sync in the gateway's constructor took the gateway down on a store that would not take a write
  (`database is locked`, `UnconfirmedLatchTests`); guarded and logged, the startup sweep's own rule.
- **Judged at landing:** the out-of-the-box consequence is real and honest — a backtest over collected Binance bars that omits `--increment`
  is REFUSED until the owner records the row, so 15 existing fixtures now declare the increment they already ran under (no run id moved);
  no fee and no minimum notional in the catalogue (COUNCIL is silent, fees stay declared per run); `TradingGateway.cs` gains a `Venues`
  property and the construction-time sync, the gate chain untouched.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `0deceab`, Release: 0 warnings, 0 errors, 17
projects; touched classes 3× → 108/108 (Unit), 33/33 (Integration), 4/4 (Fault) each; the three suites at `5bd8390` (the tip before two
docs-only commits, `src` and `tests` identical): Unit 1029 + Fault 277 + Integration 663 = 1969 passed, 0 failed, 1 skipped; names → 27
added, 0 removed; 141 test sources text. Manager's gate at `4eb8dc9 (landed as `afa49dd` after a docs-only rebase, `src` and `tests` identical)`, Release: build → 0 warnings, 0 errors; suite → 1029 + 277 + 663 = 1969 passed, 0 failed, 1 skipped; names vs `main`
→ 0 removed, 27 added (sets 1593 → 1620); scan clean (`IpcToken.Ensure()` in tests, judged); no trailers; `rev-list --count` → 0; CI run 34844839257 at `afa49dd`: all four jobs GREEN — ubuntu, macos, windows-latest and `package`.

**NOT done:** nothing at `PlaceAsync` or in the risk pass (`U-freshness`, `U-allocator-1`); no fee, no min notional, no prop rulebook, no
live instrument read from ATAS, no real venue, no `venues.json` in the repo, no card in the app's window; no box, no ATAS, no money.

## 2026-09-14 — U-data-2 landed: a second candle source behind an interface, a bar that says what it is, and validated data arrival as a wake

The second of the venue and data units (COUNCIL `:145` quality flags on bars, `:164-172` Revolut X public candles, `:89-91` data arrival among the wakes), by
one fresh builder on `docs/briefs/U-data-2.md`, rebased once (docs only). Merge `c8306d4`, 5 commits, 29 files, +2223/−352 (`Versioning.cs` **schema 18**; new
`Core/Data/CandleSource.cs`, `CandleSourceCatalog.cs`, `Provisioning/CandleSourceClient.cs`, `MarketDataService.cs` replacing `BinanceDataService.cs`; the
normaliser, reader, feed, stores, pipe server, schema, report and loop; `CONTRACTS.md` "Candle sources"; four test classes). Not the money path.

- **The collector is an interface** (`ICandleSource`: id, venue, interval, coverage target in UTC days, URL shape, publishes-a-checksum, carries-volume), Binance
  one implementation, the declarations recorded per dataset. RED (interval and target read off `BinanceArchive.Interval` and twelve months — the brief's mutant,
  which is the red here): `Expected: "5m" / Actual: "1m"`. Binance's normalised file byte-identical, measured not asserted: SHA-256 `6a5958f4…84bb` taken on
  `main`@`39d3e4f` before any product change and still the constant the test meets, header and bar count with it.
- **A per-bar quality flag** (`traded` / `midpoint_derived`), the header versioned (six columns for a source whose candles always carry volume, seven with
  `quality`), an older file reading every bar `traded`, both counts on the row. RED (the flag not produced): the FILE's midpoint rows `Expected: 4 / Actual: 0`;
  mutant (written, not counted): the ROW's count, the same figure, the assertions ordered so the two are distinguishable.
- **Never trade evidence, in words, from one definition** (`BarQuality.Note`): `data-bars` (a `quality` per bar), the backtest reply, section 8, `BarFeed`,
  `data-list`, the pipe schema, the Situation's Data line. RED (suppressed everywhere): `Failed: 5, Passed: 0`, the backtest reply `Not found: "MIDPOINT-DERIVED"`;
  mutant (removed from the backtest reply only): `Failed: 1, Passed: 4`, the same `Not found`.
- **The second source end to end against the loopback harness:** one request per period, no `.CHECKSUM` ever asked for, provenance identical (url, computed hash,
  bytes, download time), `published_sha256` EMPTY where the vendor publishes none, the dataset naming `revolut-x` — a venue now in the catalogue with NO
  instruments and `verified=false`, so a run over it is refused an increment rather than given a guess. RED (the sidecar demanded unconditionally, what the code
  did before): `Assert.NotNull() Failure: Value is null` — no dataset, no raw file; mutant (the computed hash recorded as the published one): `Expected: "" /
  Actual: "7b657e…"`. The host scan extended to the vendor's host; a separate test holds the shipped row to having NO endpoint at all.
- **`data` is a wake** for Research, raised by the app's collector, keyed by the dataset's own SHA-256; a rebuild of the same bytes raises nothing; the Situation
  names the dataset. RED: `Expected: True / Actual: False`; mutant (keyed by the attempt): `data:6a5958f4…` vs `data:2026-09-14T17:23:22…` — a paid turn every press.
- **THE ENDPOINT FACT:** this repository records NO Revolut X public-candles endpoint (`RESEARCH-REQUIRED.md:170`, the SIGNED base only, no sandbox). The shipped
  `revolut-x-public-candles` row carries an EMPTY base URL and an unverified shape (`CandleSourceCatalog.BuiltIn()`, overridable by `sources.json` in TradeAgent's
  home, none in the repo); asking it for a period is a refusal in words; no run of this repository has ever sent Revolut X a request; the first real fetch is
  the owner's, by recording the endpoint. In `CONTRACTS.md`. The response format required of the source is as unverified as its URL.
- **Judged at landing, the builder's choices kept:** items 1 and 2 one commit (they share the normaliser and the rung); `BinanceDataService` → `MarketDataService`;
  `month` kept as one PERIOD; the coverage target in UTC days (365), the actual depth computed from `first_bar`/`last_bar`; gaps counted at the SOURCE's interval
  (at one minute a clean 5-minute dataset read as 80% holes); the schema-rollback fixture now drops 18's columns too.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `4f78646`, Release: 0 warnings, 0 errors; Unit 1038 + Fault 277 +
Integration 668 = 1983 passed, 0 failed, 1 skipped; touched classes 3× → 107/107 Unit and 27/27 Integration each run; names → 14 added, 0 removed. Manager's gate
at `c8306d4` (the report commit on the gated sha, `src` and `tests` identical), Release: build → 0 warnings, 0 errors; suite → 1038 + 277 + 668 = 1983 passed, 0 failed, 1 skipped (Integration 10 m 46 s, the other leg's suite overlapping, no `Timing` red); names vs `main` → 0 removed, 14 added (`[Fact]`/`[Theory]`
1620 → 1634; method sets 1650 → 1665); scan clean (`CancellationToken`, `IpcToken.Ensure()` in tests, a hunk header's `string token`, judged); no trailers;
`rev-list --count` → 0; CI run 34877523943 at `c8306d4`: all four jobs GREEN — ubuntu, macos, windows-latest and `package`.

**NOT done, NOT verified:** no network, no box, no ATAS, no money, no order, no credential; nothing on the agent pipe collects, rebuilds or wakes; the Settings press
not seen on a screen; the second source's real URL and response shape NOT VERIFIED from here; no Revolut X connector, no `U-bars`, no Databento, no live bars.

## 2026-09-14 — U-sweep-latency-win landed: the fourth press-family fixture of the class judged first, the sweep's budget-and-latency pair sized by arithmetic

The windows-only red of draft PR #19's first attempt (run 34773625675: `SweepRequestIdTests.Every_sent_not_confirmed_leg_carries_an_unknown_record_that_will_be_
reconciled`, `Assert.NotEmpty() Failure: Collection was empty`), by one fresh builder on `docs/briefs/U-sweep-latency-win.md`. Merge `4961989`, 3 commits, test-only:
`git diff main -- src/` empty, one file (`SweepRequestIdTests.cs`, +94/−12), no assertion added, removed or altered, no `Timing` trait; draft PR #20 for the runner,
closed after; the three commits rebased at landing over `U-data-2`, which touches neither.

- **Judged first, on this Mac:** the shipped 5 s budget against a 2 s cancel left the cancel exactly 1000 ms of slack (sweep 5029 ms, both legs `sent-not-confirmed`),
  and the `RefuseBeforeSend = 1` beside it never fires — it is consulted only after the deadline check — so it is gone, with the measurement at the test. Cutting
  the budget to 4 s and nothing else reproduces the CI byte for byte: `Assert.NotEmpty() Failure: Collection was empty`, both legs `not-sent`, `attempted=0`, "the
  operation deadline passed before the simulator answered". The product's answer is honest: no product change, no RED-first test.
- **The fixture, once:** a 17 s budget against 6 s calls. A read sleeps its full latency, so at most `B − 2L = 5000 ms` can be left when the cancel takes its turn
  against the 6000 it declares — that half is arithmetic, not a clock. The runner's half: `sent-not-confirmed` rather than `not-sent` while the disk has spent under
  5000 ms, five times the old room, against 15–32 ms measured for this step over 96 windows sweeps (`U-sweep-win`) and 2234 ms for the worst bare commit
  (`U-press-win-3`). Price: the sweep IS the budget, 5 s → 17 s. Mutant (the new budget with the old 2 s latency): RED, the same `Assert.NotEmpty()`.
- **The sweep of the file, six fixtures injecting latency:** `A_leg_that_failed_before_the_wire_…` AT RISK and moved to 7 s against 4 s — its 300 ms for the composite
  commit sat behind that commit, and a 10 ms gap reproduces `Not found: "Nothing was placed or cancelled"` — with the latency reset to 0 once the sweep is over so
  its closing book read stops paying it. NOT at risk, each argued at the test: `A_sweep_pays_the_emergency_budget_once_…` and `A_five_order_sweep_answers_within_
  the_budget_…` hold their margin in front of the book read, where `BeginCompositeAsync` does one SELECT and no commit; `A_five_order_sweep_carries_a_mix_…` already
  takes `SweepBudget` with 17.75 s of room and asserts that bound itself; `The_simulators_two_latencies_add_up_…` is one direct connector call, no gateway, no database.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `0fd3f14` (`src`/`tests` identical to the code tip `aa16362`), Release:
17 projects, 0 warnings, 0 errors; Integration 3× → 663 passed, 1 skipped each (11 m 2 s, 11 m 1 s, 11 m 2 s); Unit 1029 + Fault 277 + Integration 663 = 1969
passed, 0 failed, 1 skipped; names → 0 removed, 0 added (1611 = 1611). Runner: PR #20, run 34872880789 at `0fd3f14` — windows-latest SUCCESS in 24 m 6 s (Fault
272 in 13 m 27 s, Integration 575+1 in 13 m 16 s, `Timing` 88/88 first attempt), ubuntu SUCCESS, macos FAILURE on `BridgeRoundTripTests.A_newly_arrived_silent_
peer_…` (`TimeoutException`; a file this diff does not touch, green alone in 498 ms and in all three Integration passes); run 34876672818 at `8e7e2ae`
(`src`/`tests` identical): ALL FOUR SUCCESS — windows 23 m 26 s, macos 14 m 34 s with that test green, `package` 4 m 42 s. Manager's gate at `c53aa6a` (landed as `4961989` after a
docs-only rebase, `src` and `tests` identical), Release: build → 0 warnings, 0 errors; suite → 1038 + 277 + 668 = 1983 passed, 0 failed, 1 skipped; names vs `main` → 0 removed, 0 added (sets 1665 = 1665; `[Fact]`/`[Theory]` 1634 = 1634); scan clean (`IpcToken.Ensure()`
context lines in the test file, judged); no trailers; `rev-list --count` → 0; CI run 34881215351 at `4961989`: windows-latest and macos-latest GREEN, ubuntu-latest RED outside the `Timing` category on `PeerRowTests.A_newly_arrived_silent_peer_is_not_masked_by_the_previous_peers_auth_failure` (`TimeoutException: condition was not met in time`, a file this diff does not touch; the third sighting on a third machine), `package` skipped — a hosted-runner red under step 6, the sha recorded RED, fixer briefed as `U-peer-row-ubuntu`.

**NOT done:** no product code, no `Timing` membership, no box, no ATAS, no money; the macos red of the first run is not diagnosed beyond "not this diff" — the
hosted-runner class, and a fresh fixer's if it recurs on `main`.

## 2026-09-14 — U-freshness landed: a strategy declares its timeframe, its data freshness and its maximum decision age, and the gate asks again at the wire

The first money-path unit of the council line (COUNCIL `:96-97` verbatim; `:14-15` freshness among the code-enforced gates; `:33` "never a late trade"), by one fresh
builder on `docs/briefs/U-freshness.md` on the `U-data-2` tip, rebased by the builder onto `f7c4f31` and at landing over the sweep-latency test change (`src`
untouched by both). Merge `570279b`, 9 commits, 39 files, +1695/−51 (`Versioning.cs` **schema 19** — `timeframe`, `data_freshness`, `max_decision_age` on
`strategy_version` and `strategy_promotion`, INTEGER seconds, nullable, not backfilled; the parser, `StrategyCanonical.cs`, `StrategyEvaluator.cs`, `GatewayTypes.cs`
(`IntentDecision` on `PlaceIntent`), `TradingGateway.cs` (`RefuseAStaleDecisionOrThrow`), `Referee`, section 3, `MissionLoop.cs`, `Promotions.Current` out of
`AppHost`, `STRATEGY-LANGUAGE.md`, `CONTRACTS.md`; the test classes). MONEY PATH: the dispatch gate.

- **Three declarations on the program** (`timeframe`, `data_freshness`, `max_decision_age`; durations `INT(s|m|h|d)`, 1 s–7 d; all three or none; canonicalised as
  whole seconds and hashed). RED: ``line 5: `timeframe` is not a declaration this language has``; mutant (dropped from `StrategyCanonical.Of`): `Assert.NotEqual()
  Failure: Strings are equal` — two bounds, one id. The three day-one golden ids moved once — crossover `8873b586…`→`3b336473…`, breakout `88f6586a…`→`70ec1a6e…`,
  mean reversion `eeb61430…`→`16d6192f…` — each recomputed outside the build and matched.
- **They travel onto the version and the promotion row,** taken off the frozen program the referee re-parses. RED: `table strategy_version has no column named
  timeframe` (SQLite names the first missing column, not `max_decision_age` as the brief guessed); mutant (`Referee.Verdict` reading `version.Timeframe` instead
  of the re-parsed program): `Expected: FreshnessBounds {…} / Actual: null`.
- **An intent carries the bar it was computed from and the bounds it was computed under** — `IntentDecision`, all four or nothing, `IntentDecision.From` its only
  builder. RED is a compile failure, the guard being a type that did not exist (`CS0117 … 'Freshness'`); mutant (the bar's OPEN as the decision instant): the intent
  reads one minute OLDER — `Expected: …12:01:00 / Actual: …12:00:00` (the brief said younger; the builder's correction, the magnitude exact).
- **The gate at DISPATCH,** `RefuseAStaleDecisionOrThrow` after `ReauthorizeAtDispatchOrThrow` with nothing awaited after it: past `max_decision_age` or
  `data_freshness` → `DECISION_EXPIRED`, the record stays `CREATED`, `NeedsReconciliation` false, the wire empty — a definite refusal, never an UNKNOWN. Beyond the
  brief, a decision whose bar has not closed is refused with its own sentence. RED: `String: "ok — FILLED"`, `orders at the broker: 1`; mutant (moved into
  `RiskCheckOrThrow`, above the awaited reads, the clock advanced 5 m inside the position read): `aged inside the reads: True / outcome: ok — FILLED`.
- **The owner and the roles are told:** section 3 gains a freshest-bar line per dataset and whether the promoted bound is satisfiable now, judged against the
  freshest MARKET-data bar (fixtures excluded); the Situation's data line says the same and marks fixture versus market. RED: `Not found: "newest bars:"`; mutant
  (`now − AcceptedAt` instead of `now − LastBar`): `Not found: "freshest bar 400d old"` — a year of history collected today read fresh.
- **Judged at landing:** the declarations are OPTIONAL and all-or-none (required would have refused ~120 existing program texts), so a program declaring none emits
  an intent with no `Decision` and the gate has nothing to refuse on — and NOTHING YET FORCES A PROMOTED VERSION TO DECLARE THEM. That is a hole on the money
  path: briefed as `U-promote-bounds` (the referee refuses promotion without the three), queued before any runner reaches the order path. The two bounds are two
  limits on one measurement at dispatch (the tighter binds, the refusal names which) and diverge only in the report. Four pinned tests UPDATED, none deleted.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `2902d9c`, Release: 0 warnings, 0 errors; touched classes 3× → Unit
101/101, Fault 6/6 each; Unit 1072 + Fault 283 + Integration 668 = 2023 passed, 0 failed, 1 skipped (Integration 10 m 50 s); names → 29 added, 0 removed (1680 →
1709). Manager's gate at `570279b` (the nine commits rebased over `4961989`), Release: build → 0 warnings, 0 errors; suite → 1072 + 283 + 668 = 2023 passed, 0 failed, 1 skipped; names vs `main` → 0 removed, 29 added (sets 1665 → 1694;
`[Fact]`/`[Theory]` 1634 → 1663); scan clean; no trailers; `rev-list --count` → 0; CI run 34883265713 at `570279b`: all four jobs GREEN — ubuntu, macos, windows-latest (23 m 38 s) and `package`.

**NOT done, NOT verified:** no live or paper bar feed — the gate is proved over recorded bars and `GatewayOptions.Clock`; no runner turns a `StrategyIntent` into a
`PlaceIntent`, so `IntentDecision.From` has no production caller yet; nothing reached a venue, ATAS, the box or real money; `U-flatten`, the allocator, the
connectors and the referee's verdict untouched; section 3 and the Situation never seen on a screen.

## 2026-09-15 — U-flatten-1 landed: a loss-budget breach is a durable fact the app watches for, refuses off and reports — nothing is sent yet

The first of the three `U-flatten` units briefed from a read-only survey and one Astra consult (COUNCIL `:14-15` the loss gate, `:33` "never a late trade", `:59-63`
a loss-budget event as a consequential boundary), by one fresh builder on `docs/briefs/U-flatten-1.md`, rebased by the builder onto `bc0e89c` and at landing over
two docs-only commits. Merge `1c5d71f`, 6 commits, 19 files, +2043/−13, NO schema rung — the record lives in `kv` (`LossBreach`, `LossWatchAsync`, `LossBudgetOrThrow`
and the boundary in `TradingGateway.cs`; `GatewayTypes.cs`, `GatewayOptions`, `BoundaryStore`, section 4, `MissionLoop.cs`, the Safety page, `GatewaySchema.cs`,
`WorkspaceBuilder.cs`, `USER-GUIDE.md`, `CONTRACTS.md`; four Fault and four Unit classes). MONEY PATH: the dispatch gate and the approval path.

- **The record, written once and outranking the ledger:** `loss_breach:{account}:{utcDay}` and `…:{symbol}:{utcDay}` in `kv` with the evidence; `LossBudgetOrThrow`
  refuses off it before reading the ledger and AHEAD of the zero check (a budget the owner later sets to zero does not reopen the day), after `CanIncreaseExposure`
  so a close is never refused. RED (the two throws removed): all four `LossDayClosureTests` `Assert.Throws() Failure: No exception was thrown`; mutant (the stamp
  on the local date): "breach at 22:30Z, local 23:30 / still the same UTC day: 23:30Z, local 00:30" — no exception.
- **The watch rides the health pass both hosts already run:** every `LossWatchInterval` (15 s) a fresh quote per open-position symbol is pulled OUTSIDE the gate,
  both budgets evaluated and the record written UNDER `_dispatchGate`; the mark is the executable side (bid long, ask short), younger than `MaxQuoteAge`, stamped
  with the connection epoch; `QuoteChanged` schedules one coalesced pass; the record is written only when a second DISTINCT pull within `LossBreachConfirmWithin`
  (60 s) agrees. RED (`Confirmed` forced false): 3 of 5 `LossWatchTests` `Assert.NotNull() Failure`; mutant (the confirming read off `_quotes`): "pull 3: day
  reached True, closed [loss_breach:SIM-001:2026-03-10]" — one bad print closed the day.
- **One boundary per account and UTC day** (`BoundaryKind.LossBudget`, entity the account, revision `yyyyMMdd`, default `hold`, both directors woken once), guarded so
  it cannot fail the closure. RED (the call removed): both `LossBoundaryTests` `Assert.Single() Failure: The collection was empty`; mutant (opened in the refusal
  path, keyed by the refused order): `The collection contained 2 items` after two refusals.
- **The day is told closed:** `loss_day_closed_at` and `loss_symbols_closed` on `status` (absent when open), the Situation line, the Safety row, section 4, the schema
  text, the AGENTS text, the guide, `CONTRACTS.md`. RED (`ClosureToday` forced to "nothing closed"): 4 of 6 `LossDayClosedSurfacesTests`, `Not found: "closed today to
  new risk at 12:00 UTC"`; mutant (`LossDayClosedAt` derived from the ledger figure): "figure now: 950.00 of 1000, day reached False" — reads open after a recovery.
- **Judged at landing, the builder's choices and deviations kept:** an unreadable closure row refuses `RISK_CHECK_UNAVAILABLE`, never reads as an open day; either
  budget's first confirmed closure opens the ONE boundary. (a) `_quotes` is NOT dropped on a disconnect (that would change the order-value gate and `trade pnl`):
  each quote carries a connection epoch and the watch refuses an older one; (b) the watch is stricter about a mark than the admission gate, which still values off
  `LastQuote` with no side, age or epoch filter — A NAMED GAP; (c) one test's SETUP moved, no assertion loosened. Owner-overrulable in `CONTRACTS.md`: automatic
  reopening at the next UTC midnight; `kv` until `U-protect`; 15 s and 60 s.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `1750cbd`, Release: 17 projects, 0 warnings, 0 errors; Unit 1078 + Fault 294 +
Integration 668 = 2040 passed, 0 failed, 1 skipped; touched classes 3× → Fault 26/26 and Unit 21/21 each; names → 17 added, 0 removed (1639 → 1656). Manager's
gate at `ec0f443` (landed as `1c5d71f` after docs-only rebases, `src`/`tests` identical to `1750cbd`), Release: build → 0 warnings, 0 errors; the first suite run,
under the session-limit kill of both legs (Integration 31 m 42 s, three times its length): Unit 1077/1078 (`DownloadPartBindingTests`, `Assert.Single(): 2 items`), Fault
294/294, Integration 666/669 (`ConnectorSendDeadlineTests.A_bridge_that_only_heartbeats_…` ×2, `TimeoutException` at 15 s, the `Timing` class) — CONTAMINATED, judged
so; the same build re-run alone next morning: Unit 1078 + Integration 668 = 0 failed, 1 skipped (11 m 2 s), the two classes alone 3× → 5/5 and 51/51, exit 0 on all eight; names vs `main` → 0 removed, 18 added (sets
1694 → 1712; `[Fact]`/`[Theory]` 1663 → 1680); scan clean; no trailers; `rev-list --count` → 0; CI at `1c5d71f`: GREEN on all four jobs (run 34944450004); the record commit `1577739` green on all four (34944670484); the docs-only `44f33a4` (34944735920) RED on macos — `DownloadPartBindingTests.An_install_with_no_checksum_…`, `Assert.Single(): 2 items`, the THIRD sighting, briefed `U-decision-hook-race` — and on windows — `OperatorEmergencyRecordTests.Close_all_with_a_healthy_connector_…`, 1 m 05 s, `Assert.Empty()` with one position still open, first sighting, briefed `U-close-all-win`; ubuntu green, package skipped.

**NOT done, NOT verified:** NOTHING IS SENT — no close, cancel or order leaves the gateway because of a breach, asserted at the wire (order count and position
unchanged across a closure); the flatten (`U-flatten-2`), the data-loss exit (`U-flatten-3`), stops and targets (`U-protect`), a table, the assessments' content, the
admission gate's mark validity (b); no screen, no box, no ATAS, no money. `DownloadPartBindingTests` also flaked once in the builder's DEBUG run — the loopback class, watched.

## 2026-09-15 — U-allocator-1 landed: capital is allocated to a promoted version, and the dispatch gate refuses an order past it on the one position reading

The first allocator unit (COUNCIL `:55-57` the allocator is code, `:59-63` a change of allocation is a consequential boundary, `:32-33` only a promoted version
executes), briefed from a read-only survey, built by two builders: the first killed by the 2026-09-14 session limit with items 1–4 committed and item 5 uncommitted;
the second, fresh and briefed to read the branch first, kept that work on its merits, rebased over `U-flatten-1`, finished item 5 and quoted one mutant per item.
Merge `8cb95de`, 8 commits, rebased onto `926e87e`. **Schema 20** (`U-flatten-1` took no rung; `U-allocator-2` takes 21). MONEY PATH: the dispatch gate.

- **The allocation is one immutable row** (`strategy_allocation`: id = sha256 over version id, promotion id, policy version, `max_quantity`, `max_notional`,
  currency, `effective_from`; `INSERT … ON CONFLICT DO NOTHING`, no update, no delete, no pipe op, no verb). Mutant (the clock hashed into the id):
  `The_same_allocation_recorded_twice…` → `Assert.Single() Failure: The collection contained 2 items`, the two rows differing only in `At`.
- **Written only for a version whose `Standing` reads `promoted` at that instant**, through a two-press Capital card on the Safety page the agent has no route
  to. Mutant (`Standing` replaced by "a promotion row exists"): `An_invalidated_promotion_cannot_be_allocated_capital` → `version 6b52acd9f0d5 may trade up to 1
  from 2026-09-13 12:00:00Z.`
- **An intent may name its version and the request records it:** `PlaceIntent.StrategyVersionId`; `execution_request.strategy_version_id` and `allocation_id`
  written at create, never re-derived from the parameters blob. Mutant (scraped from `parameters`): `A_rewritten_parameters_blob…` → `Expected:
  "6b52acd9f0d5…" / Actual: "a-version-that-never-placed-anything"`.
- **The ceiling is the FOURTH gate inside the dispatch gate, on the one position reading,** after `LossBudgetOrThrow` and, like it, returning before refusing
  when `CanIncreaseExposure` is false so a close is never refused; an order naming a version with no standing allocation, or past `max_quantity` /
  `max_notional`, is refused `ALLOCATION_*` — nothing sent, no row written. Mutant (evaluated above the awaited reads): `Two_placements_in_flight_together…`
  → `Assert.Equal() Failure: Expected: 1 / Actual: 2`, `connector place calls: 2`, two orders at the broker.
- **Told:** section 4 names every standing allocation (version, ceiling, currency, from when, policy version) and marks one whose promotion now reads
  invalidated `WITHDRAWN`; the Situation's promoted line says what capital stands behind it, no holdout figure. RED (the behaviour stripped, the shapes kept,
  so the failure is silence): all 5 red, `Not found: "- allocated: none"`; mutant (the WITHDRAWN mark dropped): `Not found: "WITHDRAWN: its promotion no
  longer stands"`.
- **Two defects the first full suite found, both fixed in `090c74c`:** item 3 added two columns to `Cols` but not to `TryCreateFlagged`'s VALUES list —
  `SQLite Error 1: '19 values for 21 columns'`, 43 Fault + 1 Integration red, every operator emergency press unable to write its row, and LIVE ON THE BRANCH
  under all four committed items (the first builder committed them without a full-suite run); and the rung-16 rollback fixture did not undo schema 20 —
  `duplicate column name: strategy_version_id`. No assertion loosened.
- **Choices, now in `CONTRACTS.md` with a test holding them (`3e8d399`):** capital is allocated to a promoted strategy version (COUNCIL names no subject; rule 8
  makes it the only executable thing); the ceiling is the owner's declared number, not a fraction of a balance the app does not persist; a position not
  attributable to a version counts against the ceiling anyway — it can only refuse. Deviation kept: one commit spans items 1 and 3 (one root cause in each).

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `3e8d399`, Release: 17 projects, 0 warnings, 0 errors; Unit 1097 +
Fault 304 + Integration 668 = 2069 passed, 0 failed, 1 skipped; touched classes 3× → AllocationLedgerTests 10, AllocationGateTests 10, AllocationSurfacesTests 6,
TwoPressGrantTests 27, VenueCatalogTests 8, all green; names `main` 1712 → 1741, `[Fact]`/`[Theory]` 1680 → 1709, nothing removed, nothing moved.
Manager's gate at `65ba93f` (landed as `8cb95de` after a docs-only rebase, `src`/`tests` identical), Release: build, 17 projects → 0 warnings, 0 errors; Unit 1097/1097 (18 s), Fault 304/304 (1 m 24 s), Integration 668/669, 1 skipped (11 m 4 s) → 0 failed; names vs `main` → 0 removed, 29 added (sets 1712 → 1741; `[Fact]`/`[Theory]` 1680 → 1709); scan clean; no trailers; `rev-list --count` → 0; CI at `8cb95de`: GREEN on all four jobs (run 34967151001: ubuntu, windows, macos, package).

**NOT done, NOT verified:** no runner emits a live or paper intent, so the deployment this gate ceilings does not exist — the gate is bound end to end and the
limit is stated in `CONTRACTS.md`; `docs/USER-GUIDE.md` untouched (the Capital card is the owner's only route); no screen, no box, no ATAS, no money, nothing
sent to any venue. Not this unit: parentage, exploration, retirement (`U-allocator-2`); the boundary event and the sealed assessments; a balance-derived ceiling.

## 2026-09-15 — U-peer-row-ubuntu landed: the fixture handed the pipe over instead of racing the connector's teardown; the product was right on every platform

A hosted-runner red judged under step 6 (ubuntu-latest at `4961989`, a test-only sha; the same test red once on macos-latest and once on a Mac, never twice in a
row), briefed 2026-09-14; the first fixer killed by the session limit before its first commit, a FRESH fixer re-briefed from the file on disk this morning. Merge
`69657b2`, 3 commits, TEST-ONLY (`git diff main -- src/` empty): `tests/Shared/Harness.cs` +71, `PeerRowTests.cs`, `BridgeRoundTripTests.cs`. No assertion added,
removed or altered; no timeout raised; no `Timing` membership — the verdict needs no runner clock, only that the peer reached the connector at all.

- **Judged first — the SECOND wait, on the new peer's silence; the first never timed out.** The shipped body did not reproduce in 60 iterations under 12 CPU hogs
  plus another worktree's suite. With the wait SPUN instead of polled every 50 ms — what the poll is worth where the connector's own teardown loses the CPU — it
  failed 40 in 40, all on the second wait, the row still reading the previous peer's `… could not prove it holds this installation's bridge secret (…/bridge.auth)`
  with `quiet.IsConnected = True`; and 40 in 40 recovered the moment a second client connected (hogs killed, `pgrep -x yes` → 0).
- **The measured mechanism:** off Windows a named pipe is a Unix-domain socket — a connect to the single BUSY instance succeeds in 0 ms into the backlog and dies
  when the accept loop's `finally` disposes that instance, so the next instance never sees the peer (nothing in 2000 ms) while the client still reports itself
  connected; a read on the orphan settles at once with 0 bytes, on a live peer not within 500 ms. Windows retries on `ERROR_PIPE_BUSY`, hence green there. The
  product masks nothing: the newcomer never arrived.
- **Moved, 3 of 8:** `A_newly_arrived_silent_peer_…` (the red) and `A_peer_inside_the_auth_grace_…` hand over through `HandOver.ToASilentPeer`, reconnecting on
  that end-of-stream and ONLY on it, so real masking still fails with the row quoted; `An_authenticated_peer_that_has_not_said_hello_…` through `PeerAsync`, which
  redials an unanswered challenge (8 attempts) as `Redial` already does. **Not at risk, 5 of 8:** one connection each on a fresh connector with nothing being
  disposed, where a connect that beats the accept loop gets `ENOENT`, which the client's own retry covers. **Beyond the class:**
  `BridgeRoundTripTests.A_newly_arrived_silent_peer_…_refusal`, the identical shape and the macos red of run 34872880789, fixed the same way. Green after: the
  fixed body under the same spin and load, 0 in 40.

**Verified by running (the fixer, quoted; then the manager's gate):** fixer's gate at `179e23c` (rebased onto `8cb95de` mid-flight; the first gate at `4455cd8`
green too), Release: build `--no-incremental` → 0 warnings, 0 errors; Integration ×3 → 668 passed, 1 skipped, 0 failed each; Unit 1097 + Fault 304 + Integration
668 = 2069 passed, 0 failed, 1 skipped; names vs `main` → 0 removed, 0 added. Runner: draft PR #21, run 34962898676 at `4455cd8` (ubuntu 12 m 11 s, macos
14 m 17 s, windows 41 m 6 s, package 4 m 23 s) and run 34967216164 at `179e23c`, ALL FOUR GREEN both times; the PR closed after.
Manager's gate at `69657b2` (the reported tip `036dcc0` rebased onto `3725179`, `src`/`tests` identical), Release: build, 17 projects → 0 warnings, 0 errors; Unit 1097/1097 (21 s), Fault 304/304 (1 m 24 s), Integration 668/669, 1 skipped (11 m 4 s) → 0 failed; names vs `main` → 0 removed, 0 added (sets 1741 = 1741; `[Fact]`/`[Theory]` 1709 = 1709); the scan flagged the product's own sentence "bridge secret" and the harness identifiers `cred.Secret`/`WrongSecret` — no value, judged false positives, excluded by name; no trailers; `rev-list --count` → 0; CI at `69657b2`: GREEN on all four jobs (run 34972765683: ubuntu, windows, macos, package).

**NOT done, NOT verified:** no product code, so no RED-first test and no mutant; the SHIPPED 50 ms-poll body was never reproduced on this Mac (a starved-thread-pool
attempt stalled the test host and was killed by PID) — the runner logs stay its only sighting; the two fixtures moved beside the red have no red of their own,
only the same shape and measurement; no `Timing`, no box, no ATAS, no money.

## 2026-09-15 — U-flatten-2 landed: a confirmed breach closes the book by code — openers cancelled and settled first, reduction-only enforced in code, resolved by machine only behind a flat read-back

The second `U-flatten` unit (COUNCIL `:14-15` the loss gate, `:33` "never a late trade"; `CLAUDE.md` rule 3, two-press), briefed from the day's survey and consult, the
brief amended before dispatch so the breach record stays immutable, built by one fresh builder on a worktree cut from the allocator's tip. Merge `d7f235d`, 7 commits,
17 files, +2252/−144, NO schema rung — the record is `kv`. MONEY PATH: the dispatch gate, the press mechanics, the connector's close.

- **Openers first:** on a confirmed breach every working order that could increase exposure on the scope is cancelled under the app's own press kind
  (`op-budget-cancel-`) and SETTLED before any close; then the working book is read again. RED (no flatten): `book after the flatten: [FB-1 Buy 2 FILLED | FB-3 Buy 1
  WORKING]` → `after the opener fills: ES 3`, `Expected: 0 Actual: 3`. Mutant (the cancel skipped): `[… FB-3 Buy 1 WORKING | FB-4 Sell 2 FILLED]`, `position: ES 0`,
  `after the opener fills: ES 1`. A platform that refuses the cancel leaves `closes` unchanged, ES 2, `HasUnconfirmedWork` true — its own test.
- **The reduction-only exception, enforced in code,** through ONE shared `CloseCapturedAsync` the owner's press and the app's legs both use, plus `ReductionOnlyOrThrow`
  before the wire — a leg that would reverse or open throws. RED (flatten and sweep disabled): `Assert.NotNull() Failure: Value is null`. Mutant (routed through
  `CloseAsync`): `still open: ES: TRADING_PAUSED_UNRECONCILED; still open: NQ: …`, `book: [ES 2 | NQ 1]`. Second mutant (size check removed): 3 red, `Expected:
  typeof(GatewayDeniedException)` twice, `closes on the wire: 0 -> 1`. `LOSS_BUDGET_REACHED` stays the caller's answer, asserted.
- **Resolved by machine, or paused:** a leg resolves only when its close is terminal AND a fresh read-back says flat; the outcome is its own write-once `kv` row
  `loss_flatten:{connector}:{account}:{utcDay}`, and the breach row's bytes are compared before and after. RED (judged, never cleared): `unconfirmed work: True`.
  Mutant (terminal alone, no read-back): the REJECTED leg over `position: ES 1` resolves, `Expected: True Actual: False` on `HasUnconfirmedWork`.
- **Crash recovery,** keyed on the ABSENCE of the outcome record, never on the composite, never while anything is unreconciled. RED (no sweep): `Assert.NotNull()
  Failure: Value is null`, ES 2 still open. Mutant (keyed on the composite): the same red — a composite begun and killed is never finished.
- **Told:** `status.loss_flatten` `flat` / `unresolved`, the Situation, section 4, the Safety row, and every sentence that promised "nothing is closed for you" rewritten
  to "TradeAgent CLOSED YOUR OPEN POSITIONS …". RED: 4 of 5, `Not found: "CLOSED YOUR OPEN POSITIONS"`, `Not found: ""loss_flatten":"flat""`. Mutant (`flat` from the
  composite's ok): reads flat over an open ES 1, `Expected: False Actual: True`.
- **Choices, in `CONTRACTS.md`:** (a) the key carries the CONNECTOR, not the brief's `{account}:{utcDay}` — an account id is unique only within a platform and a PAPER
  flatten must not answer for a LIVE closure; the account is read off the breach record and the flatten refuses unless the gateway operates it; (b) the cancel takes
  every working order on an instrument about to be closed, reducers included — a protective sell is a reducer only while the long exists; (c) the app clears its OWN
  flags behind a read-back and restores `ExecutionCapability` on `ReconcileAsync`'s two lines, else a correct flatten is a manual outage; (d) only app legs take the
  extra position read before the wire; the owner's press is byte for byte what it was. Deviations kept: the shared close and `ReductionOnlyOrThrow` landed in item 1's
  commit; two `LossWatchTests` wire assertions changed from "nothing is sent" to one close, the right symbol, a flat read-back — stronger, not looser; `ClosedDay`'s
  SETUP moved; two tests RENAMED because their NAME stated the withdrawn promise.
- **Two limits, named:** `FakeConnector.ClosePositionAsync` re-reads and re-sizes itself, so a wire-level REVERSAL cannot be produced against the simulator — the flow
  test states no close reached the connector and the row says why, and `ReductionOnlyTests` holds the arithmetic directly. Inherited: the shared close path catches a
  definite `ConnectorRejectedException` in the same arm as a timeout and records UNKNOWN — the safe direction, not this unit's to change.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `ebd6b2a` (rebased onto `3725179`), Release: 17 projects, 0 warnings, 0 errors;
Unit 1102 + Fault 320 + Integration 668 = 2090 passed, 0 failed, 1 skipped; touched classes 3× → LossFlattenTests 9, ReductionOnlyTests 7, LossWatchTests 5,
LossFlattenSurfacesTests 5, LossDayClosedSurfacesTests 6, 15 runs exit 0; names 1741 → 1758, 19 added, 0 removed, 2 renamed. Manager's gate at `d7f235d` (the reported tip `5de5445` rebased onto `25f79b7`, the branch's patch-id identical before and after), Release: build, 17 projects → 0 warnings, 0 errors; Unit 1102/1102 (18 s), Fault 320/320 (1 m 23 s), Integration 668/669, 1 skipped (11 m 4 s) → 0 failed; names vs `main` → 2 removed = the 2 RENAMED, 19 added (sets 1741 → 1758; `[Fact]`/`[Theory]` 1709 → 1726); scan clean; no trailers; `rev-list --count` → 0; CI at `d7f235d`: GREEN on all four jobs (run 34974556123: ubuntu, windows, macos, package).

**NOT done, NOT verified:** no reopen of a closed day — no cooldown, no strikes, no receipts (`U-reopen-1`); no data-loss exit (`U-flatten-3`); no stops or targets
(`U-protect`); no schema, no table, no assessments; NOT VERIFIED anywhere but this Mac — no box, no ATAS, no real money; every order in every test went to the
simulator through `RecordingConnector`, the wire counts asserted.

## 2026-09-15 — U-decision-hook-race landed: the decision hook is scoped per call, so no test can read another class's line through the process-wide static

A hosted-runner red judged under step 6 — `DownloadPartBindingTests.An_install_with_no_checksum_writes_the_decision_and_the_reason`, `Assert.Single(): 2 items`, three
sightings (a builder's DEBUG run, the manager's contaminated gate at `ec0f443`, and macos-latest at the DOCS-ONLY `44f33a4`, run 34944735920) — briefed this morning
and fixed by one fresh fixer. Merge `4809e58`, 3 commits, 6 files, +117/−42. The product gains ONE optional parameter and no behaviour: `recordDecision` defaults to
null, and null means `Downloader.RecordDecision` exactly as before (`AppHost.cs:520` untouched). Not the money path.

- **The producer, named, and the red reproduced on this Mac both ways:** `CandleSourceTests.A_source_that_publishes_no_checksum_records_no_published_hash_and_says_why`
  is the ONLY other test that swaps the static; it installs `BTC-USD-5m-2026-06-09_2026-09-06.csv`, and whichever class assigned the hook second owned both classes'
  decisions. The two classes alone, Release, 10 runs → 4 red: `Assert.Single() Failure: The collection contained 2 items / Collection: ["Installed ATASPlatform.exe
  without checking it aga"…, "Installed BTC-USD-5m-2026-06-09_2026-09-06.csv wit"…] at DownloadPartBindingTests.cs:line 208`. The two TESTS alone, 10 runs → 10 red
  the other way: `Assert.Contains() Failure: Filter not matched in collection / Collection: []` at `CandleSourceTests.cs:line 298`.
- **Scoped per call, not serialised:** `Action<string>? recordDecision` on `Downloader.DownloadAsync`, `CandleSourceClient.FetchPeriodAsync`/`CollectAsync` and both
  `MarketDataService.CollectAsync`; the static is the app's default only. Both tests pass their own sink; `Assert.Single(recorded)` unchanged; NO `[Collection]` added.
  The same 10 + 10 runs after the fix → 0 red. A guard test, `No_test_assigns_the_process_wide_decision_hook`, scans `tests/` through
  `SuiteReachesNoVendorTests.TestSources()`; mutant (the swap put back at `CandleSourceTests.cs:288`) → RED: "…pass `recordDecision:` on the call instead:
  CandleSourceTests.cs:288".
- **The sweep of every other `finally`-restored shared state in `tests/` — none at risk, none changed:** `CoreTests.cs:371-381` `CultureInfo.CurrentCulture` (per async
  flow, not process-wide); `CoreTests.cs:626-644` the `RuntimeCatalog` override FILE (already under `[Collection(VendorOverrideFiles.Name)]`); `CoreTests.cs:730-739`
  `PathEntry` PATH (the suite's only PATH writer, its class serial); `AgentEnvironmentTests.cs:26-36,59-70` two `TA_TEST_*` names nothing else reads;
  `PressAtomicityTests.cs:105` `Pressing`, an `AsyncLocal` declared in the test.

**Verified by running (the fixer, quoted; then the manager's gate):** fixer's gate at `aaf9a20` (rebased onto `25f79b7`), Release: build `--no-incremental` → 0 warnings,
0 errors; Unit 1098/1098, Fault 304/304, Integration 668 passed + 1 skipped of 669, each `--no-build` to a file → 0 failed; Unit 5× → 0 failed (1098 each time);
names vs `main` → 0 removed, 1 added (1756 → 1757); scan clean before each commit; no trailers. Manager's gate at `00a316f` (the reported tip `f314cdf` rebased onto `473178c`, the branch's patch-id identical before and after; landed as `4809e58` after a docs-only rebase, `src`/`tests` identical), Release: build, 17 projects → 0 warnings, 0 errors; Unit 1103/1103 (19 s), Fault 320/320 (1 m 26 s), Integration 668/669, 1 skipped (11 m 5 s) → 0 failed; names vs `main` → 0 removed, 1 added (sets 1758 → 1759; `[Fact]`/`[Theory]` 1726 → 1727); scan clean; no trailers; `rev-list --count` → 0; CI at `4809e58`: GREEN on all four jobs (run 34977342255: ubuntu, windows, macos, package).

**NOT done, NOT verified:** no `[Collection]`; no assertion loosened or widened; no test deleted; no product behaviour changed and so no RED-first product test; no
Windows box, no ATAS, no money. The hosted runners' verdict on this fix is the merge sha's CI, recorded below.

## 2026-09-15 — U-promote-bounds landed: the referee refuses to judge a version whose frozen program declares no timeframe, data freshness or maximum decision age

The hole `U-freshness` disclosed at its landing (the three declarations are optional and all-or-none in the language; a program declaring none emits an intent with no
`Decision`, so the dispatch gate has nothing to refuse on), briefed the same hour on 2026-09-14 as one light item, built by one fresh builder. Merge `a816f1a`,
2 commits (the item, then the report), 7 files, +445/−16. No schema rung. MONEY PATH: what may execute.

- **No bounds, no promotion.** `Referee.Verdict` refuses a version whose RE-PARSED FROZEN PROGRAM declares none of the three, in a sentence naming `timeframe`,
  `data_freshness` and `max_decision_age`; no promotion row is written, and `AllocationLine` marks a live allocation whose promotion carries none. RED (the bound-less
  fixture is profitable over the holdout and was frozen before it, so it met every clause and promoted): `Failed …PromotionBoundsTests.A_version_that_declares_none_of_
  the_three_is_not_promotable_and_no_row_is_written [32 ms] / a version declaring none of the three execution bounds was judged`; section 4 and the contract RED beside
  it — `Not found: "NO EXECUTION BOUNDS"`, `Not found: "## U-promote-bounds"`. MUTANT (the check applied when `Standing` is READ instead of at the verdict): three tests
  red; the one that separates the mutant from the fix is `A_promotion_already_recorded_without_bounds_still_stands` → `Assert.Equal() Failure: Expected: "promoted" /
  Actual: "refused"` — the brief predicted `Assert.Null()` on the row; what the row-level claim actually separates is the promotion recorded BEFORE this rule, which
  the mutant withdraws and the fix leaves standing (the builder's correction, kept).
- **Choices, all in `CONTRACTS.md` under `## U-promote-bounds` and pinned by a test:** (a) the refusal is a failure to JUDGE (`RefereeVerdict.Ok` false) — no promotion
  row at all, not even a recorded `refused`, which is a verdict about evidence where this is about the submission; (b) asked BEFORE the verdict charge, so a bound-less
  version spends no verdict budget, reads no held-back bar, opens no boundary and wakes nobody; (c) `RequestVerdict` and `HoldoutFeed` unchanged — the rule is about
  promotion, not about who may look at evidence; (d) the bounds come off the recorded source re-parsed, never off `strategy_version`'s columns; a source that no
  longer parses falls through to the refusals `Verdict` already had; (e) NOTHING already promoted is invalidated — COUNCIL `:35` spends invalidation on a changed
  ASSUMPTION, and this build's opinion about what may be judged is not one, so `Promotions.Standing` is untouched; (f) section 4 marks such an allocation
  `NO EXECUTION BOUNDS`, names the three and says the dispatch gate cannot judge how stale its decisions are — the opposite of `WITHDRAWN`, never both; (g) the
  language is unchanged, the three stay optional and all-or-none.
- **Fixtures, no assertion loosened, no test deleted:** `RefereeVerdictTests`' `ProfitableText`/`LosingText` and `VerdictOverPipeTests`' `ProgramText` gained the three
  declarations, because every test in those classes is about what happens after the referee agrees to judge; their version ids move; the duplicate
  `ProfitableWithBounds` constant folded into `ProfitableText`, its test keeping its name and every assertion.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `51bf6e3` (the same commit before a rebase onto a one-line record no test reads;
`git diff --stat 51bf6e3 cfb03b0 -- src tests docs` empty), Release: 0 warnings, 0 errors; Unit 1108 + Fault 320 + Integration 668 = 2096 passed, 0 failed, 1 skipped
(Integration 11 m 5 s); touched classes 3× → PromotionBoundsTests 5, RefereeVerdictTests 12, VerdictOverPipeTests 1, all `Passed!`; names 1759 → 1764, 5 added,
0 removed. Manager's gate at `a816f1a` (the reported tip; `src`/`tests` identical to the code tip `cfb03b0`; 0 behind `main` `8092667`), Release: build, 17 projects → 0 warnings, 0 errors; Unit 1108/1108 (21 s), Fault 320/320 (1 m 25 s), Integration 668/669, 1 skipped (11 m 4 s) → 0 failed; names vs `main` → 0 removed, 5 added (sets 1759 → 1764; `[Fact]`/`[Theory]` 1727 → 1732); scan clean; no trailers; `rev-list --count` → 0; CI at `a816f1a`: GREEN on all four jobs (run 34982400055: ubuntu, windows, macos, package).

**NOT done, NOT verified:** the declarations stay OPTIONAL in the parser (requiring them would refuse program texts this installation has already accepted; the brief
excludes it); no runner on the order path, so no live or paper intent exists for this rule to gate; no promotion already recorded withdrawn, by code or by hand;
section 4's new mark never seen on a screen, only asserted in the rendered text; no box, no ATAS, no money, no order, no venue.

## 2026-09-15 — U-reopen-1 landed: a closed scope reopens by code only when it has earned it — a computed instant, a fresh flat book, a write-once receipt, a clock that cannot step it open

The owner's decision of the morning (option B — the day reopens by code, never "until the owner has read it" — "only if it has exquisite logic and edge case
handling"), shaped by ONE Astra consult (final answers only) and a read-only survey, briefed as two units, built by one fresh builder on a worktree cut from the
flatten's tip. Merge `bacd4e3`, 7 commits, no schema rung — the records are `kv`. MONEY PATH: the dispatch gate, the approval path, the watcher tick.

- **Closure is a state, not a key.** `OpenClosures` scans `loss_breach:{account}:` across days; a scope is closed from `ConfirmedAt` until a receipt exists for THAT
  record; no second breach row or boundary while closed (one episode); every flatten key off the breach's day. RED (the moved rollover test): `Assert.NotNull()
  Failure: Value is null` at 00:30Z. Mutant (`DayClosed` back to the day key): the same, `next UTC day : 2026-03-11 00:30Z`.
- **Eligibility is computed, the reopen is a receipt.** `LossReopen.EligibleAt` = max(the next UTC midnight, `ConfirmedAt` + `GatewayOptions.LossMinClosure` = 24 h);
  the receipt `loss_reopen:{connector}:{account}:{utcDay}` is written only by the tick, under `_dispatchGate`, through a new `Database.AddKvOnce` (`INSERT … ON CONFLICT
  DO NOTHING`); the admission gate never writes it. RED: `Assert.Single() Failure: The collection was empty`, `eligible, no tick : LOSS_BUDGET_REACHED`. Mutant (the
  gate writes it on first admission): `Assert.Throws() Failure: No exception was thrown`. Second mutant (the midnight term dropped): `2h closure -> eligible 11:00Z`.
- **Flatness is fresh evidence.** The receipt needs a position read on the current epoch flat for the scope, the flatten record `Flat` with no opener unsettled, no
  unresolved `op-budget-*` leg, `HasUnconfirmedWork` false; an unvaluable book stays out of `Measured`. Three REDs, each `Assert.Empty() Failure` with `reopened :
  [loss_reopen:fake:SIM-001:2026-03-10]` — a flagged leg over a flat book, an opener that would not cancel, a position re-opened by hand. Mutant (flatness from the
  flatten record alone): `book : [ES 1]`.
- **A clock that stepped back cannot reopen.** `loss_clock_high_water:{connector}:{account}`, monotone, raised only while something is closed, seeded at `Close`;
  `loss_clock_suspect` written once. RED: `high water / now : 2026-03-12 16:00Z / 2026-03-11 14:00Z`, a receipt written. Mutant (the mark upserted every tick): the same.
- **A parked proposal dies with the breach.** `ApproveAsync` declines — `APPROVAL_PREDATES_LOSS_BREACH`, never `LOSS_BUDGET_REACHED` — any `AWAITING_APPROVAL` request
  written before the latest breach of its scope, above the mode and the budgets, after the reopen too. RED: `Assert.Throws() Failure: No exception was thrown` — the
  stale proposal reached the wire. Mutant (compared to `EligibleAt`): a proposal written DURING the closure refused instead.
- **Told, the old sentences withdrawn:** `status.loss_reopens_at` / `loss_reopen_held` / `loss_reopened_at`, the Situation, the Safety row, section 4 (`reopens:` /
  `reopened:`), `GatewaySchema`, `AGENTS.md`, `USER-GUIDE.md`, `CONTRACTS.md`; "midnight UTC" gone from the schema text. RED (shapes kept): 3 of 4, `Expected:
  2026-03-11T12:00:40Z / Actual: null`. Mutant (`loss_reopened_at` off the receipt's presence alone): `after the second close: closed=12:02:20Z reopened=12:01:40Z`.
- **Choices, in `CONTRACTS.md`:** 24 h; the connector in every new key (the breach key untouched — INHERITED LIMIT: keyed by account, so a platform sharing an account
  id inherits its closures); the closure readers keep their `Today` names though they answer across days; the clock mark only while closed; "held" judged from rows
  this app wrote, never a platform read, so `loss_reopens_at` is the EARLIEST, not a promise; `loss_reopened_at` absent whenever anything is closed; `LatestBreach`
  skips an unparseable row while `OpenClosures` still throws on an unreadable STANDING one; the watch reads the platform on a tick even with both budgets zero while
  a closure stands, so zeroing a budget after a breach cannot close an account for ever. Deviations kept: item 2's mutant watched on ONE admission (`_dispatchGate`
  serialises placements); item 6's RED is the shapes-kept strip. Three tests MOVED (renamed, re-pinned): the rollover test now pins that a 22:30Z breach still refuses
  at 00:30Z and two days later and trades only on a receipt; a next-day breach while closed opens NO second boundary; a flatten's own rows do not refuse the next day.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `3bec613` (rebased onto `e7d7ebe`), Release `--no-incremental`: 17 projects,
0 warnings, 0 errors; Unit 1111 + Fault 329 + Integration 668 = 2108 passed, 0 failed, 1 skipped (Integration 11 m 5 s); touched classes 3× → Fault
LossDayClosureTests 4, LossBoundaryTests 2, LossFlattenTests 9, LossReopenTests 6, LossReopenApprovalTests 3; Unit LossDayClosedSurfacesTests 6, LossReopenRuleTests 4,
LossReopenSurfacesTests 4, every run green; names 1759 → 1776, 20 added, 0 removed, 3 moved. Manager's gate at `bacd4e3` (the reported tip `36478c8` rebased over `U-promote-bounds`, the branch's patch-id identical before and after; landed as `bacd4e3` after a docs-only rebase, `src`/`tests` identical), Release: build, 17 projects → 0 warnings, 0 errors; Unit 1116/1116 (22 s), Fault 329/329 (1 m 41 s), Integration 668/669, 1 skipped (11 m 4 s) → 0 failed; names vs `main` → 3 removed = the 3 MOVED, 20 added (sets 1759 → 1776; `[Fact]`/`[Theory]` 1727 → 1744 before the promote-bounds rebase); scan clean; no trailers; `rev-list --count` → 0; CI at `bacd4e3`: GREEN on all four jobs (run 34983998037: ubuntu, windows, macos, package).

**NOT done, NOT verified:** no schema rung; `LossMinClosure` is not a setting, no strikes, no review card, no bounded director hold (`U-reopen-2`); no data-loss exit
(`U-flatten-3`); no stops (`U-protect`); no screen, no box, no ATAS, no money — every wire assertion is `RecordingConnector`.

## 2026-09-15 — U-loopback-listener-mac landed: a failed bind leaves the managed listener closed, so every loopback fixture now borrows a free port and retries from a fresh listener

A hosted-runner red judged under step 6 — `DownloadPartBindingTests.A_stale_part_from_another_download_is_never_resumed_into_this_one`, `ObjectDisposedException:
'System.Net.HttpListener'` in 8 ms, macos-latest at the DOCS-ONLY `5415a8f` (run 34967255830); the family's earlier sighting is `UpdateTrustTests` at `:3738` — briefed
this afternoon and fixed by one fresh fixer. Merge `4b0c6e8`, 3 commits, 8 files, +148/−59, TEST-ONLY (no product file touched). Not the money path.

- **The mechanism, measured on .NET 10.0.11 / macOS 26.5.1, not assumed:** a `Start()` that fails to bind leaves the `HttpListener` CLOSED, not stopped — `IsListening`
  false, and the NEXT TOUCH of `Prefixes` throws `ObjectDisposedException: Cannot access a disposed object. Object name: 'System.Net.HttpListener'.` The fixture's
  retry died at `Prefixes.Clear()` without ever reaching `Start()` again: the twenty attempts were one, and the first collision was fatal. Both bind failures end there —
  in-process `…conflicts with an existing registration on the machine`, and over a socket another process holds `Address already in use` (the `:3738` wording).
- **Reproduced here in the real shape:** the four classes sharing the band 18000–19999, 20 runs → 1 red, in 1 ms, at `HttpListener.get_Prefixes() … FakeArchive..ctor
  FakeArchive.cs:line 47` — `DatasetLedgerTests.A_dataset_whose_normalised_file_was_edited_is_rejected_too`, the CI red's shape in another class. After the fix, 20/20.
- **Fixed once, in `tests/Shared/Loopback.cs`** (linked into all three test projects): a port the OS says nothing on the machine is on, from a `TcpListener` at 0; borrow
  and bind in one critical section; every retry from a FRESH listener, which is why it hands back a started listener rather than a number. MOVED: `DownloadPartBinding-
  Tests.Vendor` (the CI red), `FakeArchive` (the red above), `FakeProvider` (the same loop; its band 21000–23999 luckier, not safer), `UpdateTrustTests.Server` (its `Bind`
  borrowed a port properly but retried on the same instance — the `:3738` sighting; `Bind` deleted). LEFT ALONE: `SuiteReachesNoVendorTests`, which owns no listener;
  it gains the guard `No_test_builds_its_own_loopback_listener`, RED on two mutants (`FakeArchive.cs:30`, `DownloadPartBindingTests.cs:42`) — its first version matched
  only the named constructor, missed the target-typed `= new()` every fixture used, and PASSED its mutant; the fixer caught that and rewrote it.

**Verified by running (the fixer, quoted; then the manager's gate):** fixer's gate at `9eb4040` (rebased onto `28438b4`), Release: build `--no-incremental`, 17 projects →
0 warnings, 0 errors; Unit 1117/1117 (19 s), Fault 329/329 (1 m 23 s), Integration 668 passed + 1 skipped of 669 (11 m 5 s), each `--no-build` to a file → 0 failed;
Unit 5× → 0 failed, 1117 each; names vs `main` → 0 removed, 1 added (1781 → 1782; `[Fact]`/`[Theory]` 1749 → 1750). Manager's gate at `4b0c6e8` (the reported tip `9d1f20a` rebased onto `3c3b3f2`, `src`/`tests` identical), Release: build, 17 projects → 0 warnings, 0 errors; Unit 1117/1117 (19 s), Fault 329/329 (1 m 25 s), Integration 668/669, 1 skipped (11 m 5 s) → 0 failed; names vs `main` → 0 removed, 1 added (sets 1781 → 1782; `[Fact]`/`[Theory]` 1749 → 1750); scan clean; no trailers; `rev-list --count` → 0; CI at `4b0c6e8`: GREEN on all four jobs (run 34988018140: ubuntu, windows, macos, package).

**NOT done, NOT verified:** no product file touched, so no RED-first product test; no `[Collection]`; no assertion loosened or widened; no test deleted; no draft PR — the
hosted runners' verdict on this fix is the merge sha's CI, recorded below; no box, no money.

## 2026-09-15 — U-reopen-2 landed: two strikes hold the scope for the owner, the durations are his to narrow with two presses, and every record says which rule applied
The second half of the owner's reopen decision, built by one fresh builder on a worktree cut from the reopen-1 tip. Merge `8714db1`, 6 commits, 22 files,
+3080/−35, no schema rung — the records are `kv`. MONEY PATH: a release lets orders flow.

- **Two strikes hold the scope.** At CONFIRMATION, inside `Close` under `_dispatchGate`: the scope's earlier breach rows inside the window write
  `loss_hold:{connector}:{account}[:{symbol}]:{utcDay}` through `AddKvOnce`, naming every episode; `HeldBy` answers above the instant, the tick writes no receipt, the
  breach row is untouched. RED: `Assert.Empty() Failure`, `second reopen : [loss_reopen:fake:SIM-001:2026-03-13]` — the receipt written 24 h later. Mutant (the
  window measured at reopening): `window from : 2026-03-17 (first episode 2026-03-10)`, `reopened : […2026-03-13]` — the strike aged out while the scope was closed.
- **Release is the owner's, two-press, with a note.** `ReleaseHold` writes `loss_release:…` once with the note and the episodes; a *Reopen after review* card on the
  Safety page calls it and nothing else does — no verb, no pipe op, no setting. RED (shapes kept): `release : ok=False []`. Mutant (the release writes the receipt):
  `Assert.Null() Failure: Value is not null` with a leg still flagged. Second mutant (one press): the hold lifts on the first click, 3 of the card's tests red.
- **The durations are the owner's, and every record snapshots what applied.** `RiskPolicy.LossMinClosureHours` (24) and `LossStrikeWindowDays` (7) on the Safety
  page; `Widenings` names a SHORTENED one — the only fields on that page where smaller is the grant — so `BuildSaveLimits` arms the second press. The breach record
  carries `MinClosure`/`StrikeWindowDays` at `Compose`, the receipt carries both, a record with no snapshot is judged by the fixed defaults. RED (the live setting
  governing): `Expected: 2026-03-11T09:00:00 / Actual: 2026-03-11T00:00:00`, `snapshot : closure=none window=none`. Mutant (the snapshot dropped from the receipt):
  `receipt rule : closure=00:00:00 window=0`.
- **The directors' hold is bounded — and the request channel is NOT there.** No line of `ExtensionFor` reads `BoundaryRow.Disposition`: the boundary's `hold` is
  written once by policy and never revised, so reading it as holding is a closure with no end. An instant moves only for a `loss_extend:…` row with an `Until` naming
  THIS episode's boundary id, clamped to one closure length, applied once by key, void on a release, shown with its end. RED (the boundary's `hold` read as
  holding): `reopens at 9999-12-31 23:59Z, said the directors are holding it`. Mutant (re-applied each tick): `asked / applied: 2026-04-10 / 2026-03-12`, `reopened : []`.
  The brief's escape hatch taken: no method takes a disposition from a director, so NOTHING IN THIS BUILD WRITES AN EXTENSION ROW — the code and `CONTRACTS.md` say so.
- **Told:** `status.loss_held_for_review` / `loss_released_at` / `loss_closure_rule`; the Situation and Safety row carry the hold, the extension with its end, the rule
  and the release with the note quoted; section 4 gains `held for review`, `closure rule`, `held open until` and `loss closures in the window` (one line per episode:
  scope, instant, the rule THAT episode was judged by, outcome); `GatewaySchema`, `AGENTS.md`, `USER-GUIDE.md`, `CONTRACTS.md`. RED (shapes kept): 3 of 3 red. Mutant
  (the note optional): `blank note : ok=True — Released.`, `Assert.False() Failure`.
- **Choices, in `CONTRACTS.md`:** the connector in all three new keys; one press releases EVERY hold standing on the account, each row carrying the same note; an
  unreadable hold answers HELD and an unreadable breach row still counts as a strike through its key, while an unreadable release or extension answers "none"; a hold
  that cannot be WRITTEN is logged at error and does not fail the closure; no press re-imposes a hold; a release with nothing held is refused in words; `ReleaseHold`
  takes no dispatch gate; section 4's listing window is the LIVE setting while every decision follows a snapshot. One assertion RE-PINNED, not loosened: the surfaces
  test pinned "AT LEAST 24 HOURS" and now pins the sentence that replaced it (the length is the snapshot). A released scope with a flagged leg refuses
  `TRADING_PAUSED_UNRECONCILED`, the unreconciled pause being ahead of the loss gate — pinned with the wire count.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `83e52c4` (rebased onto `69c44d7`), Release `--no-incremental`: 17 projects,
0 warnings, 0 errors; Unit 1127 + Fault 345 + Integration 668 = 2140 passed, 0 failed, 1 skipped (11 m 7 s); touched classes 3× → Fault LossStrikeTests 3,
LossReleaseTests 4, LossRuleSnapshotTests 5, LossDirectorHoldTests 4; Unit LossHoldSurfacesTests 3, TwoPressGrantTests 34, LossReopenSurfacesTests 4, all 21 runs
green; names 1782 → 1807, 25 added, 0 removed, 0 moved. Manager's gate at `ddf81fc` (the reported tip; `src`/`tests` identical to the gated `83e52c4`; landed as `8714db1` after a docs-only rebase, `src`/`tests` identical), Release: build, 17 projects → 0 warnings, 0 errors; Unit 1127/1127 (19 s), Fault 345/345 (1 m 36 s), Integration 668/669, 1 skipped (11 m 5 s) → 0 failed; names vs `main` → 0 removed, 25 added (sets 1782 → 1807; `[Fact]`/`[Theory]` 1750 → 1775); scan clean; no trailers; `rev-list --count` → 0; CI at `8714db1`: GREEN on all four jobs (run 34992913399: ubuntu, windows, macos, package).

**NOT done, NOT verified:** no schema rung; no allocation ladder (a hold is per scope; aggregate enforcement and attribution do not exist — `CONTRACTS.md`); no
data-loss exit (`U-flatten-3`); no stops (`U-protect`); nothing writes an extension; no screen shot, no test constructs `SafetyPage` (the card's two-press behaviour is held
at its factory, as every card on that page is); no box, no ATAS, no money — every wire assertion is `RecordingConnector` or the simulator.

## 2026-09-15 — U-close-all-win landed: a close-all press with two legs makes fourteen durable commits before its second close, and the fixture's budget is now sized by that arithmetic

A hosted-runner red judged under step 6 — `OperatorEmergencyRecordTests.Close_all_with_a_healthy_connector_closes_each_position_once_and_records_each`, windows-latest at
the DOCS-ONLY `44f33a4` (run 34944735920), 1 m 05 s, `Assert.Empty()` with one position still on the fake broker; first sighting — briefed this morning and fixed by one
fresh fixer. Merge `0dbc121`, 3 commits, 5 files, +103/−22, TEST-ONLY (`git diff main -- src/` empty; every `Assert.` line in `DispatchRecoveryTests.cs` byte-identical
to `main`; no test added, renamed or removed). The settle's-own-commits family of `U-sweep-win`, `U-press-settle-win` and `U-press-inflight-win`, one term further along.

- **Judged from the code — one path, and nothing in the product is wrong:** ONE absolute deadline is opened per press (`RiskReducingScope.Begin`), and everything
  between it and a leg's wire call is durable SQLite at `synchronous=FULL`. When it expires inside those commits the NEXT leg's close takes `Wire(ct, "positions")` →
  `HonourTheOperationDeadline`'s `left <= 0` branch, which throws BEFORE the book is read while `RecoveryConnector` has already counted the call — exactly `two targets,
  two closes reached the connector, one position still on the fake broker`; the shared `CloseCapturedAsync` records the leg UNKNOWN and flags it, so no leg is abandoned
  unflagged. Reproduced on this Mac (a throwaway hook before `tx.Commit()` holding every commit 2234 ms, the worst commit measured on that runner image, reverted): the
  real fixture on `PressBudget` fails byte for byte, the press record reading `op-close-…-0 ES FILLED needsRecon=True` beside `op-close-…-1 NQ UNKNOWN … the operation
  deadline had already passed and nothing was sent to the simulator`.
- **Why 65 s, from the red run's own trx:** a close-all makes 9 durable commits for one position and 14 for two (12 before the second leg's close reaches the wire), a
  cancel-all 10 and 13 — `5 + 5×legs` bounds both. The red's fixture makes 35 in all; 65.58 s over 35 is 1874 ms a commit, inside the 16–2234 ms band measured on that
  image, on a run whose whole Fault project averaged 8.6 s a test against 0.26 s here. Fourteen press commits at that rate are 26 s against a 20 s budget: not a budget
  a stalled disk ate, but a budget never sized for two legs.
- **Fixed once, at the test:** `Unresolved.PressBudgetFor(legs) = (5 + 5×legs) × 2234 ms` → 23 s, 34 s, 45 s, both commit counts and the runner's worst commit stated
  there. GREEN under the 2234 ms stall: the press takes 31 s, both legs FILLED, `positions: []`. Mutant (the flat `PressBudget` put back, same stall): RED with the
  `Assert.Empty()` message above. All ELEVEN presses in the file moved, each site naming its own legs — seven with two legs, four with one (20 s short of 23 s by the
  same arithmetic). No `Timing` trait: the verdict is still what the press DID, and this number exists to keep the runner's clock out of it.
- **NOT moved, named as a debt:** five multi-leg presses outside this brief's file still on the flat 20 s — `UnknownCloseTests.cs:259`, `EmergencyPressTests.cs:224,252`,
  `PressInFlightTests.cs:151`, `LossFlattenTests.cs:206` — one token each; briefed as `U-press-budget-legs` the same hour, after `U-flatten-3` (which touches the last file).

**Verified by running (the fixer, quoted; then the manager's gate):** fixer's gate at `6b3c1cf` (rebased onto `69c44d7`), Release: build `--no-incremental`, 17 projects →
0 warnings, 0 errors; Fault 3× `--no-build` → 329/329 each; Unit 1117/1117, Fault 329/329, Integration 668 passed + 1 skipped of 669 → 0 failed; names vs `main` → 0
removed, 0 added. Runner: draft PR #22, run 34988626096 at `6b3c1cf` — ALL FOUR JOBS GREEN (windows-latest Fault 324/324, the fixture 5.43 s; ubuntu; macos; package);
the PR closed after. What that run does NOT prove: its disk was healthy (Fault 821 s at a 2.08 s median against the red's 2485 s at 6.19 s), so it re-ran the fixture,
not the stall — the stalled-disk claim rests on the local reproduction and its mutant. Manager's gate at `dac7430` (the reported tip `d08fc15` rebased over `U-reopen-2`, the branch's patch-id identical before and after; landed as `0dbc121` after a docs-only rebase, `src`/`tests` identical), Release: build, 17 projects → 0 warnings, 0 errors; Unit 1127/1127 (22 s), Fault 345/345 (1 m 24 s), Integration 668/669, 1 skipped (11 m 7 s) → 0 failed; names vs `main` → 0 removed, 0 added (sets 1782 = 1782; `[Fact]`/`[Theory]` 1750 = 1750); scan clean; no trailers; `rev-list --count` → 0; CI at `0dbc121`: GREEN on all four jobs (run 34994421185: ubuntu, windows, macos, package).

**NOT done, NOT verified:** no product code and no product mutant; no `Timing` trait; no assertion loosened or touched; the five presses above not moved; no box, no
ATAS, no money.

## 2026-09-16 — U-flatten-3 landed: a position nobody can value is a state with a clock, and past the owner's bound it is closed under its own reason, never as a budget breach

The third `U-flatten` unit (Astra's line of 2026-09-14, adopted: a persistent data loss needs its own pre-authorised response with a reason distinct from a breach,
and no claim of unattended protection without it), built by one fresh builder on a worktree cut from the reopen-2 tip, killed by the 2026-09-15 session limit with
all three items committed and resumed the next day with one message. Merge `c441120`, 4 commits, no schema rung. MONEY PATH: the watcher tick, the app's legs.

- **Unavailable is a state with a clock.** One episode row per open instrument (`valuation_unavailable:{connector}:{account}:{symbol}`), `Since` written on the first
  tick that cannot value it and carried forward until a FRESH in-epoch executable mark ends it; the risk-increasing orders on that symbol cancelled ONCE per episode
  through `-2`'s cancel mechanics under `op-valuation-cancel-`. RED (tracking disabled): `episode : (none)`, `book after ten ticks : [FB-1 Buy 2 FILLED | FB-3 Buy 1
  WORKING]` — nothing recorded, the opener still resting. Mutant (`CanBeValued` takes any cached quote): `episode : (none)` — unavailability never ages past one tick.
- **The data-loss exit, bounded and distinct.** Past `RiskPolicy.ValuationLossExitMinutes` (15 out of the box; zero is OFF and the WIDEST value, so lengthening is the
  two-press widening) of CONTINUOUS unavailability with the connection UP, the position closes through `-2`'s cancel/close mechanics under its own reason
  `VALUATION_LOST`, its own write-once episode-keyed record `loss_valuation_exit:{connector}:{account}:{symbol}:{yyyyMMddTHHmmssZ}` and its own
  `BoundaryKind.ValuationLoss`. RED: `before the bound : ES 2, closes on the wire 0`, `exit record : (none)`, `position after : ES 2`. Mutant (the reason folded
  into the breach record): `Assert.Null() Failure … Actual: LossBreachRecord { … Why = TradeAgent closed today to new risk at 12:01 UTC: the day was down 1000 USD …}`
  — the report claims a budget breach that never happened. Connection DOWN: nothing sent, the clock runs on, its own fixture (`closes on the wire : 0 -> 0`, `ES 2`).
- **Disclosure.** Section 4 and `CONTRACTS.md` state all four thresholds with the numbers in force (tick 15 s, confirmation 60 s, quote age 30 s, exit bound 15 min),
  that each is a point at which TradeAgent INTERVENES, and that none bounds the realised loss — gap, spread, slippage, unreported fees. RED: `Not found: "none of them
  is a maximum loss"` while the report still printed `loss allowance left … of 1,000.00 USD`. Mutant (printed only when the budget is zero): `Not found: "what these
  limits are and are not"`. Sampling delay MEASURED on this Mac (Release, simulator connector, one open position): one reading of the open book 4.849 / 4.934 /
  4.862 / 4.899 ms — the app's own arithmetic, not a platform round trip — on top of a watch interval of up to 15 s; the report prints the live figure.
- **Choices, in `CONTRACTS.md`:** a `VALUATION_LOST` exit does NOT close the day and is NOT a strike — nobody measured a loss; the instrument stays refused new risk only
  while it cannot be valued. The exit keyed by EPISODE, not `{utcDay}` (the brief's key): two silences on one instrument on one date are two events, and a day key would
  make the second write-once insert answer false and leave a position nothing would ever close. Two new app press kinds and a separate boundary kind, so no surface
  dates a valuation exit by a budget. The episode row is an upsert the watch refreshes under the dispatch gate; the EXIT is write-once at the SQL layer. The
  precautionary cancel takes only orders that could INCREASE exposure — a resting protective order is the one thing bounding an unmeasurable position — and clears
  the flags it wrote behind its own read-back, or a data outage would pause the whole product. Deviations kept: item 2's product code in item 1's commit (the episode
  and its exit share `ValuationLoss.cs`, neither half compiles alone); `CancelOpenersForBreachAsync` and `AccountForTheFlattenAsync` generalised to take the press
  kind rather than copied; the RED and mutant quotes taken at the pre-rebase tip `efa239b` (the only difference to the gated tip is the test-only `U-close-all-win`).

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `c7926ab` (rebased onto `910795b`), Release `--no-incremental`: 17 projects,
0 warnings, 0 errors; Unit 1132 + Fault 350 + Integration 668 = 2150 passed, 0 failed, 1 skipped; touched classes 3× → 13 classes, 39 runs, every one exit 0;
names 1807 → 1817, 10 added, 0 removed, 0 moved. Manager's gate at `c441120` (the reported tip, 0 behind `main` `910795b`; `src`/`tests` identical to the gated `c7926ab`), Release: build, 17 projects → 0 warnings, 0 errors; Unit 1132/1132 (19 s), Fault 350/350 (1 m 26 s), Integration 668/669, 1 skipped (11 m 7 s) → 0 failed; names vs `main` → 0 removed, 10 added (sets 1807 → 1817; `[Fact]`/`[Theory]` 1775 → 1785); scan clean; no trailers; `rev-list --count` → 0; CI at `c441120`: GREEN on all four jobs (run 35095715885: ubuntu, windows, macos, package).

**NOT done, NOT verified:** NOT VERIFIED anywhere but this Mac — no box, no ATAS, no real money; every order reached the simulator through `RecordingConnector`,
wire counts asserted. No schema. The denial while a valuation is missing stays ACCOUNT-wide (one unvaluable position makes `LossBudget.Read` answer unknown for the
whole account; a per-symbol denial needs a decomposable figure, which it is not). Nothing re-tries or reconciles an exit that could not confirm — one attempt per
episode, then the rows stay flagged and the account pauses for a person. Stops and targets (`U-protect`), the Situation block, `AGENTS.md`, `USER-GUIDE.md` and the
agent schema untouched beyond `status.loss_valuation_lost` / `loss_valuation_exit`, added so an agent can see a position is on its way to being closed.

## 2026-09-16 — U-allocator-2 landed: declared parentage outside the hash, variants charged to their lineage, an exploration reserve read where it is written, a retirement that fences and erases nothing, the directors' forecasts sealed against a baseline

The second allocator unit (COUNCIL `:271-272` and `:218-226`), built by one fresh builder, killed by the 2026-09-15 session limit with items 1–3 committed and item 4
in uncommitted files, resumed the next day with one message. Merge `0d6a268`, 6 commits, 19 files, +1996/−76. **Schema 21.** NOT the money path: nothing here
reaches `PlaceAsync`.

- **`strategy_version.parent_version_id`** — declared by the submitter through the `backtest` op's `parent`, self-referencing, outside the version hash, the first
  declaration stands. RED: `SQLite Error 1: 'table strategy_version has no column named parent_version_id'`. Mutant (the parent inferred from the role's last version):
  `Assert.Null() Failure: Value is not null / Actual: "6b52acd9f0d5…"`.
- **A variant is charged to its ancestry's campaign lineage** (`strategy_trial.charged_to`, walked to the eldest charged ancestor then forward through renewals; counted
  under either number, so it can only tighten). RED (the item reverted): `Assert.Equal() Failure: Expected: 2 / Actual: 1`. Mutant (charged to the child's own
  campaign): `Expected: 2 / Actual: 1` — the lineage count unmoved by a variant.
- **The exploration reserve:** two pots summing to the trial budget, both counts read inside `RegisterTrial`'s own `Database.Write`. RED: `Expected: False / Actual: True`
  (a third exploration trial taken with 3 of 5 unspent) and, on `Barrier(2)`, `Expected: 1 / Actual: 2`. Mutant (the reserve refused by the pre-run look, not at the
  gate): `Expected: 1 / Actual: 2`.
- **The retirement disposition,** opened on the `U-council-concurrent-2` machine, its default frozen at open to bounded replacement, applied by `ApplyDue`, fencing a
  NEW trial and a NEW verdict only. RED: `Expected: False / Actual: True` — a retired candidate registered a further trial. Mutant (retirement clearing the allocation
  row): `Assert.NotNull() Failure: Value is null` — the deployed strategy lost its capital. The brief's item-4 RED corrected by the builder: nothing in the product
  applied a retirement at all, so no code path could delete or rewrite a trial row; the reachable RED is the absent fence, "erases nothing" held row by row.
- **The directors' own record:** `RECOMMENDATION:` and `BASELINE:` declared on the assessment and sealed with it, against `review_baseline` written in the same UPDATE
  as the disposition; section 8 prints one line per director per settled boundary, silence included. RED: `Expected: "promoted" / Actual: null`, `Not found:
  "recommended deploy, code applied hold — d"…`. Mutant (the declared baseline read at review time): `Expected: "unjudged" / Actual: "promoted"` — every forecast reads
  correct.
- **Choices, in `CONTRACTS.md`:** the candidate is a strategy VERSION and the retirement default is bounded replacement (a successor whose promotion stands), not
  "promoted ⇒ keep" — under the latter a retired candidate could never hold capital and COUNCIL `:223`'s "never kills a deployed strategy" would be unreachable;
  `CampaignExplorationBudget` = 50 of 200, clamped to `[0, trial_budget]`, a campaign opened with none declared recording its whole budget; a parent claim is
  unverifiable but creates NO allowance; a loss-budget boundary has no measurable baseline and none is required. Deviations: `TrialRefusal` gained a `versionId`
  parameter (two call sites updated, no assertion changed); three exact column-list assertions extended and still exact; the assessment fixtures now declare the two
  lines the app requires. The rung-16 rollback fixture undoes 21 as well. The builder's own note: a `git checkout --` reverting a mutant after the resume wiped the
  uncommitted item-4 `BoundaryStore.cs` changes; reconstructed from its scratchpad fragments, rebuilt, re-tested, then committed.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `e7add72` (rebased onto `910795b`), Release `--no-incremental` with bin/obj
deleted first: 17 projects, 0 warnings, 0 errors; Unit 1140 + Fault 345 + Integration 668 = 2153 passed, 0 failed, 1 skipped; six touched classes 3× → VersionLineage
6, RetirementBoundary 7, BoundaryLedger 9, CampaignLedger 20, CouncilRelay 20, VenueCatalog 8, every run `Passed!`; names 1807 → 1820, 13 added, 0 removed.
Manager's gate at `0d6a268` (the reported tip `0572fbd` rebased over `U-flatten-3`; the branch's patch differs from before the rebase by ONE context line — flatten-3's `ValuationLoss` boundary kind added beside this unit's addition in `BoundaryStore.cs` — every changed line identical), Release: build, 17 projects → 0 warnings, 0 errors; Unit 1145/1145 (20 s), Fault 350/350 (1 m 26 s), Integration 668/669, 1 skipped (11 m 6 s) → 0 failed; names vs `main` → 0 removed, 13 added (sets 1817 → 1830; `[Fact]`/`[Theory]` 1785 → 1798); scan clean; no trailers; `rev-list --count` → 0; CI at `0d6a268`: GREEN on all four jobs (run 35097514221: ubuntu, windows, macos, package).

**NOT done, NOT verified:** nothing in this build calls `OpenRetirement` — the policy deciding WHEN a candidate is put up is about a population; every part of
COUNCIL `:219-222` whose subject is a TEAM or CANDIDATE AGENT (heritable candidate definition, diversity budgets, bounded births and turnover, probation, rollback, a
cross-candidate selection protocol) is named in `CONTRACTS.md` as waiting on an entity this build does not have. No verb, no pipe op, no UI for retirement;
`USER-GUIDE.md` untouched; no screen, no box, no ATAS, nothing sent to any venue.

## 2026-09-16 — the third milestone review of the money path at `c441120`: five HIGH, three MED, all executed; Codex's four hypotheses all verified

Run as `docs/HOW-WE-BUILD.md` says, the last step before any release: one fresh Opus reviewer told to break the money path at a named sha (`docs/briefs/REVIEW.md`,
worktree `review` detached at `c441120`, probes on `review-probes-c`), and Codex `gpt-5.6-sol` read-only on the same sha in its own worktree, in parallel and
uncoordinated. The findings, the probe output quoted, the guards that held, the ranked UNVERIFIED list and what was not done are in `docs/REVIEW-2026-09-16.md`;
Codex's answer is its `## Codex` section. **HIGH 5 · MED 3 · LOW 0 · UNVERIFIED 7 — every finding an executed refutation.**

- **HIGH 1** `LossBreach.cs:144-152` — a closure on a venue-qualified symbol (`ES:H6`, `BINANCE:BTCUSDT`) is written and invisible to every reader: `ScopeOf` splits
  the key on `:`, so `OpenClosures` skips the row and the closed scope trades. **HIGH 2** `TradingGateway.cs:783-794`, `Pnl.cs:124`, `FillStore.cs` — the fill ledger is
  scoped to no account and no platform; one account's profit keeps another trading past its budget, one account's loss closes an account that never traded. **HIGH 3**
  `TradingGateway.cs:6299`, `LossBreach.cs:179-261` — a PAPER breach flattens the LIVE book after a platform switch: the check is `account.Id == breach.Account` and the
  record carries no connector. **HIGH 4** `TradingGateway.cs:4786-4790` — `ApproveAsync` re-runs two of the four position gates and drops the capital gate and the
  unresolved-reducer refusal: a parked order goes out over a withdrawn ceiling, a parked reduce doubles a close. **HIGH 5** `GatewayPipeServer.cs:658,702`,
  `AgentGrants.cs:118-130` — a launch grant is verified once at `hello`; an expired, revoked or turn-ended grant keeps placing orders on the socket it said hello on.
- **MED 6** `TradingGateway.cs:2508,2605-2660`, `LossReopen.cs:135-148` — one forward clock step ends a closure on the next tick, against what the contract and the
  code's own doc claim; rated MED because nothing an agent can reach moves the clock. **MED 7** `TradingGateway.cs:3793` — the per-order limits (allowlist,
  `MaxOrderQuantity`, `MaxNotionalPerOrder`, quote age) are decided above `_dispatchGate` and never re-asked at the wire. **MED 8** — the two agreeing pulls are keyed
  by the UTC day, so a pair straddling midnight writes no breach at all.
- **Codex** (read-only; its sandbox refused MSBuild's temp directory, `MSB1025`, so it executed nothing) offered four UNVERIFIED hypotheses; handed to the reviewer
  after its own hunt, all four VERIFIED: C1 = 5, C2 = 3, C3 = 4 (both arms), C4 = 6. **Least proven, the reviewer's order:** whether any attached platform names an
  instrument or account with a colon (1's defect executed, its reachability on ATAS not); a second close after an exit throwing between its close and its record
  (UNVERIFIED 1, read only); everything the bridge half does — `AtasStrategyAdapter`, `CoidWitness`, `AdapterTeardown` not opened.

**Verified by running (the reviewer, quoted):** probes branch `review-probes-c` @ `58aa4fa`, two test files, no product change; Release build `--no-incremental` → 0
warnings, 0 errors; full suite → Unit 1132 + Fault 362 + Integration 669 = 2163 passed, 0 failed, 1 skipped (2150 on `main` plus 13 probes); the probe classes alone
12/12 and 1/1. The findings are not fixed on `main`: each HIGH becomes a fix unit before any release (briefed in `docs/briefs/` the same hour, 1+2+3 as ONE class fix —
the identity of a loss-line scope), MED 6+7+8 one batch unit; fixes are not re-reviewed.

**NOT done, NOT verified:** no box, no ATAS, no real money, no screen, no mutation testing, no bridge code read; the updater, the Doctor, the relay's task delivery,
the venue override loader, the second candle source, the backtest and the referee not probed; Codex ran no test. Nothing fixed, nothing pushed by the reviewer.

## 2026-09-16 — U-press-budget-legs landed: the five multi-leg press fixtures outside the close-all file take the per-leg budget, their legs measured rather than read

The debt `U-close-all-win` named, briefed as one test-only item and fixed by one fresh fixer. Merge `ee08704`, 2 commits, TEST-ONLY (`git diff main -- src/` empty;
no `Assert.` line touched; no test added, renamed or deleted). Not the money path.

- **Legs MEASURED, not read off the fixture:** a throwaway probe printed each press's own capture count (`OperatorCancelAllAsync`'s `captured`, `CloseCapturedAsync`'s
  `captured`, `CancelWorkingOrdersAsync`'s `targets`) beside the running test's name over the whole Fault project, then was reverted.
- **Moved, one token each:** `UnknownCloseTests.cs:261` 2 legs (ES+NQ) → `PressBudgetFor(2)`; `EmergencyPressTests.cs:261` 2 legs (both orders rest, both cancels are
  the verdict) → `For(2)`; `PressInFlightTests.cs:153` 2 legs (ES waits, NQ closes) → `For(2)`; `LossFlattenTests.cs:52`, the shared `Ready` behind nine flattens, 2 legs
  (the daily breach closes ES and NQ; the resting-opener fixture cancels one order then closes one position inside the SAME deadline) → `For(2)`; and
  `EmergencyPressTests.cs:229` ONE leg, not the brief's two (pk-1 rests and pk-2 fills, so the cancel-all captures one working order and the close-all one position,
  each under its own deadline) → `For(1)`, 23 s — the fixer's correction of the brief, kept.
- **Swept (`grep -rn 'PressBudget' tests/`):** every other flat-20 s site is ONE leg, measured, and left flat — nineteen sites across `UnknownCloseTests`,
  `EmergencyPressTests`, `PressInFlightTests`, `PressAtomicityTests`, `ValuationLossTests`, `LossDirectorHoldTests`, `LossReleaseTests`, `LossReopenTests`,
  `LossReopenApprovalTests`, `LossRuleSnapshotTests`, `LossStrikeTests`; none at risk of the two-leg arithmetic, though 20 s is under the 23 s one leg gets — not this
  item. **Named, not moved, outside `PressBudget`:** `IntegrationTests/SweepRequestIdTests.cs:290` `SweepBudget = 20 s` feeds ten AGENT sweeps (pipe `cancel-all` /
  `close-all`, not a press), measured up to SIX legs; a different constant in a different project — a debt stated, not touched.
- **It bites, quoted.** With a throwaway hook holding EVERY `Database.Write` commit 2234 ms (reverted, `git status` clean of it),
  `A_confirmed_daily_breach_closes_every_open_position_and_the_book_reads_flat` on `PressBudgetFor(2)`: GREEN, `Passed! - Failed: 0, Passed: 1 … Duration: 1 m 58 s`.
  Mutant (the flat `PressBudget` put back, same stall): RED — `Assert.Equal() Failure: Expected: 2 / Actual: 1` at `LossFlattenTests.cs:223`, `leg : op-budget-close-…-1
  NQ captured 1 -> UNKNOWN filled 0`, `book : [NQ 1]`, the leg refused with `the operation deadline had already passed and nothing was sent to the simulator`. The fixture
  tried FIRST, `UnknownCloseTests.cs:261`, did NOT bite under the same stall on either budget (its ES leg is refused by the settle's own failed read before it can spend
  the budget) — that site's move rests on the measured two legs and the arithmetic, stated as such.

**Verified by running (the fixer, quoted; then the manager's gate):** fixer's gate at `33bffa7` (rebased onto `6c7dd09`; rebased once more after the gate onto the
docs-only `2dcf8f3`, `git patch-id --stable` identical), Release: build `--no-incremental` → 0 warnings, 0 errors; Fault 3× → 350/350 each; Unit 1145/1145; Integration
668 passed + 1 skipped of 669 → 0 failed; names vs `main` → 0 removed, 0 added. Manager's gate at `9855529` (the reported tip; landed as `ee08704` after a docs-only rebase, `src`/`tests` identical), Release, overlapping the reviewer's own probe suite: build, 17 projects → 0 warnings, 0 errors; Unit 1145/1145 (21 s), Fault 350/350 (1 m 25 s), Integration 668/669, 1 skipped (11 m 3 s — its normal length, so not contaminated) → 0 failed; names vs `main` → 0 removed, 0 added (sets 1830 = 1830; `[Fact]`/`[Theory]` 1798 = 1798); scan clean; no trailers; `rev-list --count` → 0; CI at `ee08704`: GREEN on all four jobs (run 35102359965: ubuntu, windows, macos, package).

**NOT done, NOT verified:** no product code and no product mutant; no assertion loosened or touched; no test deleted; no `Timing` trait; the one-leg flat sites and the
Integration `SweepBudget` family not moved; no box, no CI run of its own (the merge sha's CI is recorded below), no ATAS, no money.

## 2026-09-16 — U-grant-liveness landed: a launch grant is re-verified on every frame, and a grant that ends closes the connection that proved it

The fix unit for the third review's HIGH 5 (an expired, revoked or turn-ended grant kept placing orders on the socket it said hello on, while a NEW connection with
the same grant was refused `IPC_UNAUTHENTICATED`), built by one fresh builder with the reviewer's probe `C1` as its RED. Merge `105f3c1`, 2 commits, no schema.
MONEY PATH: authority on the agent-facing pipe.

- **Liveness at every frame, and an ending that reaches the socket.** Every frame that reaches the role gate is re-verified against the token the connection PROVED at
  hello — never against `req.Grant`, or a peer could swap in another live grant per frame; an ended grant is refused outright with the code and words a fresh
  connection gets, and the connection closes (not downgraded to roleless, so reads go too). `AgentGrants.Ended` fires for every way a grant stops being live, outside
  the register's lock, possibly twice for one token, a throwing subscriber never stopping the next; the server subscribes in `Start` and unsubscribes in `DisposeAsync`;
  natural expiry is enforced on the frame, not by a timer; `Revoke` raises `Ended` whether or not this register held the token. RED (the probe, renamed
  `An_ended_grant_places_no_further_order_on_the_connection_it_authenticated`, a fourth ending added): all four arms red at the wire — `[Expired] before=SENT after=SENT
  orders after=1 new connection=IPC_UNAUTHENTICATED`, `[Revoked]` and `[DisposedAndLapsed]` identical, `[DisposedInsideTheGrace] … new connection=ACCEPTED`; and
  `An_expired_grant_sends_no_close_all_…`: `close-all after, on that connection: SENT`, `mutating calls at the wire after: 1`. Mutant 1 (`Grants.Ended += OnGrantEnded`
  taken out of `Start`, the per-frame check kept): `[DisposedInsideTheGrace] 1 order(s) reached the broker AFTER the turn ended` while the other three arms read
  `after=IPC_UNAUTHENTICATED orders after=0` — the finding's second half exactly: only the ending that deliberately keeps the token in the register, the turn ending
  inside the 60 s grace, needs the socket closed to be an ending at all. Mutant 2 (the check applied only to `Ops.Buy`): `the gateway served a close-all on a
  connection whose launch grant had expired an hour earlier`, `mutating calls at the wire after: 1`.
- **The `Grace` stays,** documented where it is declared: a call already in flight, and a `trade` launched a moment before the turn ended; a connection inside a call
  is marked, answers that frame, then closes — cutting it would report a failure for an order that may already be at the broker. The RED test asserts in the same
  breath that a FRESH connection is still ACCEPTED inside the grace, so the closure cannot be mistaken for permission to delete the grace. The peer-image rule is not
  re-run per frame: the process behind an accepted pipe cannot change, and re-running would hash the `trade` image off disk in the path of every order. All in
  `CONTRACTS.md` under `U-grant-liveness`.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `174fa5b` (rebased onto `c1ac6cd`), Release `--no-incremental`: 0 warnings,
0 errors; Unit 1145 + Fault 350 + Integration 670 = 2165 passed, 0 failed, 1 skipped; `GrantLivenessTests` 3× → 2/2 each; names 1798 → 1800, 2 added, 0 removed.
Manager's gate at `105f3c1` (the reported tip `79c0853` rebased onto `1b8f408`, `src`/`tests` identical), Release, beside another builder's suite: build, 17 projects → 0 warnings, 0 errors; Unit 1145/1145 (22 s), Fault 350/350 (1 m 26 s), Integration 670/671, 1 skipped (11 m 6 s) → 0 failed; names vs `main` → 0 removed, 2 added (sets 1830 → 1832; `[Fact]`/`[Theory]` 1798 → 1800); scan clean; no trailers; `rev-list --count` → 0; CI at `105f3c1`: GREEN on all four jobs (run 35108591315: ubuntu, windows, macos, package).

**NOT done, NOT verified:** no schema; no box, no order placed anywhere; the pipe ACL and token, the turn budget and the harness worker's key untouched; the in-process
worker path (`CallAsync`) unchanged — it presents no grant and has none to end; no new permission reaches the agent-facing pipe, only a refusal.

## 2026-09-16 — U-scope-identity landed: a loss-line scope is (connector, mode, account, symbol) on every row, read off the row, and the fill ledger is scoped to the operating pair

The fix unit for the third review's HIGH 1, 2 and 3 — three findings with one root cause, the loss line not knowing which scope it was about — built by one fresh
builder with the reviewer's probes `P1`, `P1b`, `P1c`, `P3`, `P3b`, `C2` as its REDs. Merge `f16c485`, 4 commits. **Schema 22** (`fill.connector`). MONEY PATH: the loss
gate, the flatten, the ledger the budget is enforced off.

- **The scope is on the row.** Every loss-line record carries connector, mode, account, symbol and day as fields, and every reader (`OpenClosures`, `LatestBreach`,
  `ClosureHistory`, `PriorBreaches`, `SymbolsClosedToday`, the strike count) scans by prefix then parses off the ROW, never `Split(':')`; the key keeps `:` and each part
  is ESCAPED at minting (`%`→`%25`, `:`→`%3A`) — a DEVIATION from the brief's "refused when minted", kept: a refusal would delete the loss budget on exactly the venues
  whose convention is `VENUE:SYMBOL`, while the escape is total and reversible, and a name with neither character maps to itself, so nothing on disk moves. RED on
  `4bb0846`: `A_venue_qualified_symbols_closure_is_visible_to_every_reader` (P1) `Assert.NotNull() Failure: Value is null`; P1b `Expected: Tuple ("2026-03-10", "ES:H6") /
  Actual: null`; P1c `Expected: 1 / Actual: 8` (closed and flattened eight times); the builder's own `A_closure_written_under_an_older_builds_key_is_still_seen` red too.
  Mutants: `OpenClosures` back to `ScopeOf(key)` → the older-key test red; `LossBreach.Part` not escaping → P1b red.
- **A breach is bound to its platform and mode.** `FlattenForBreachAsync` refuses when the record's connector OR mode differs from the gateway it runs on, and the
  killed-flatten sweep never re-runs another platform's breach; a record with neither field (written before this unit) still CLOSES and is never FLATTENED. RED:
  `A_paper_breach_never_flattens_another_platform_or_another_mode` (C2) `Expected: 0 / Actual: 1` — one cancel reached the live platform. Mutants: the connector half
  dropped → the `("atas", still paper)` arm red; the mode half dropped → `A_killed_paper_flatten_never_re_runs_in_live_mode_on_the_same_platform` red.
- **The fill ledger is scoped.** `fill.connector` at schema 22, nullable, NOT backfilled (no honest backfill exists); `LedgerPnl` reads the operating (connector, account)
  pair through `ScopedFills`; the average-cost book is keyed by (account, symbol); `trade pnl`, the Performance card, section 4 and its `fills today` count print the
  pair's figure and name it; rows with no connector are `unattributed fills` beside every figure and in none of them; with no account selected the ledger reads
  empty. RED: `One_accounts_loss_never_closes_another_accounts_day` (P3) `Expected: 0 / Actual: -1000.00`; P3b `Expected: True / Actual: False`; the builder's own
  `One_accounts_sell_never_closes_another_accounts_buy` `Expected: 0 / Actual: 100`. Mutants: `ScopedFills` back to `_fills.Since()` → P3 red; the book keyed by symbol
  alone → the last one red.
- **Beyond the brief, named:** `RecordingConnector` gained an optional connector id (additive, default unchanged); `LossHoldRecord` and `LossExtensionRecord` gained
  `Mode`; two existing fixtures updated with NO assertion changed (stamping the connector and mode `Compose` writes; the connector every fill carries); the rung-16
  rollback fixture undoes 22; the `Inherited limit` sentence of `U-reopen-1` and the `U-reopen-2` choices line replaced in `CONTRACTS.md` by what is now true.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `6dcb84c` (on `e4a19dc`), Release `--no-incremental`: 0 warnings, 0 errors; Unit
1146 + Fault 361 + Integration 670 = 2177 passed, 0 failed, 1 skipped; touched classes 3× → LossScopeIdentityTests 11, LossFlattenTests 9, DailyReportTests 10,
VenueCatalogTests 8, PnlScopeTests 1, PnlTests 11, all pass; names 1832 → 1841, 9 added, 0 removed, 0 moved. Manager's gate at `f16c485` (the reported tip, 0 behind `main` `e4a19dc`; `src`/`tests` identical to the product tip `6dcb84c`), Release, beside another builder's suite: build, 17 projects → 0 warnings, 0 errors; Unit 1146/1146 (22 s), Fault 361/361 (1 m 26 s), Integration 670/671, 1 skipped (11 m 7 s) → 0 failed; names vs `main` → 0 removed, 9 added (sets 1832 → 1841; `[Fact]`/`[Theory]` 1800 → 1809); scan clean; no trailers; `rev-list --count` → 0; CI at `f16c485`: GREEN on all four jobs (run 35112314289: ubuntu, windows, macos, package).

**NOT done, NOT verified:** the sweep's second attempt after an exit throws (UNVERIFIED 1); the bridge; a per-symbol valuation denial; no box, no ATAS, no real money,
no screen. The sweep's own connector-and-mode filter has NO mutant of its own — the flatten's refusal masks it — so it is log hygiene and the refusal is the guard,
asked directly in the test. The two arms naming the new record fields cannot compile on the base by construction; their red-first evidence is C2's quoted red plus
each guard's mutant.

## 2026-09-16 — U-approve-gates landed: an approval runs every position gate a placement runs, on its own single reading, and is attributed to the allocation that authorised it

The fix unit for the third review's HIGH 4 (`ApproveAsync` re-ran two of the four position gates and dropped the capital gate and the reconciliation refusal, so a
parked order went out over a withdrawn ceiling attributed to a superseded allocation and a parked reduce doubled a close), built by one fresh builder with the
reviewer's probes `C3a` and `C3b` as its REDs. Merge `9aeea56`, 2 commits, no schema. MONEY PATH: the approval path.

- **One gate sequence, two callers.** `TradingGateway.PositionGatesOrThrow(intent, positions, account, requestId, reference, ct)` runs the open-position cap, the
  unresolved-reducer refusal, the loss budgets and the allocation ceiling in that order; `PlaceAsync` and `ApproveAsync` each take their own single `GetPositionsAsync`
  reading inside `_dispatchGate` and hand it in; the approval writes the allocation the sequence answers with (`ExecutionRequestStore.Attribute`) before dispatch. RED
  on `1b8f408` (the probes given assertions — they only logged): `A_parked_order_approved_after_its_capital_was_withdrawn_is_refused_as_a_fresh_one_is` → `a FRESH
  order now: ALLOCATION_EXCEEDED / the PARKED order, approved: SENT`, `orders that reached the wire: 1`; `A_parked_reduce_approved_over_an_unresolved_reducer_…` →
  `Expected: "CLOSE_UNRESOLVED" Actual: "SENT"`, `position at the broker: ES 1 (it was ES 3)`; `An_approval_is_attributed_to_the_allocation_that_authorised_it_…` →
  `Expected: "b40e87b4…" Actual: "9500f0aa…"`. Mutant 1 (the approval path back on the old two-gate sequence): the same three red. Mutant 2 (`AllocationId` not
  reassigned): `the record's allocation_id: 9500f0aa…`, the superseded row. Control: `A_parked_reduce_with_nothing_unresolved_still_approves_and_reaches_the_wire` → `SENT`.
- **Choices, in `CONTRACTS.md` under `U-approve-gates`** (step 7 of "An approval is a dispatch decision…" split, step 8 naming the four gates): the signature takes
  `reference` and `ct` beyond the brief's four, because the ceiling's value arm multiplies the price the risk check already trusted and a second quote read inside the
  gate would be a second answer to one question; the sequence answers with the `AllocationRow` and each caller writes it; `Attribute` is the only update naming
  `allocation_id` and matches only an `AWAITING_APPROVAL` row, so an order the wire has seen can never be re-attributed (the field's doc corrected); null is a value —
  a proposal whose allocation lapsed is recorded as attributed to nothing; a MODIFY runs none of the four; `LOSS_BUDGET_REACHED`, `APPROVAL_PREDATES_LOSS_BREACH`,
  `ALLOCATION_*` and `CLOSE_UNRESOLVED` unchanged as the callers' answers, the budgets below the reducer refusal, each still starting at `CanIncreaseExposure`.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `799a6e3` (rebased onto `e4a19dc`), Release `--no-incremental`: 0 warnings,
0 errors; Unit 1145 + Fault 354 + Integration 670 = 2169 passed, 0 failed, 1 skipped; touched classes 3× → ApprovalPositionGateTests 4, ApprovalReauthorizationTests
30, AllocationGateTests 10, all pass; names 1832 → 1836, 4 added, 0 removed. Manager's gate at `c1fde96` (the reported tip `0381399` rebased over `U-scope-identity`, the branch's patch-id identical before and after; landed as `9aeea56` after a docs-only rebase, `src`/`tests` identical), Release: build, 17 projects → 0 warnings, 0 errors; Unit 1146/1146 (21 s), Fault 365/365 (1 m 26 s), Integration 670/671, 1 skipped (11 m 6 s) → 0 failed; names vs `main` → 0 removed, 4 added (sets 1832 → 1836; `[Fact]`/`[Theory]` 1800 → 1804, before the scope unit's own additions); scan clean; no trailers; `rev-list --count` → 0; CI at `9aeea56`: GREEN on all four jobs (run 35113915643: ubuntu, windows, macos, package).

**NOT done, NOT verified:** no schema; the per-order limits above the gate (MED 7 — `U-review-med`), the approval TTL and the mode re-check untouched; nothing sweeps;
no box, no money — every test is the simulator behind `RecordingConnector`, nothing proven on Windows.

## 2026-09-16 — U-review-med landed: a closure is held against a monotone reading, the per-order limits are asked again at the wire, and a breach's sighting is keyed by its scope

The third review's three MED findings as one batch, built by one fresh builder with the reviewer's probes `C4`, `P2`, `P4` as its REDs, after every HIGH fix had
landed — so item 2 went INTO `PositionGatesOrThrow`, the shared gate `U-approve-gates` made, and serves both callers. Merge `f9f5878`, 4 commits, no schema.
MONEY PATH: the reopen, the dispatch gate, the breach confirmation.

- **A closure is held against a monotone reading as well as the wall clock.** The clock mark carries `GatewayOptions.Clock.GetTimestamp()` (`Stopwatch` in production);
  eligibility requires the closure to have RUN on both; a forward wall-clock step of more than four watch intervals (60 s shipped) with no matching monotone elapse
  is suspect like a backward one — `loss_clock_suspect`, refused, said in `status`; a restart is a new gateway run, never suspect, and its gap is ADDED to the instant
  off the `LossBreachRecord.ClockUnverified` baseline (null on an older row measured from zero); a bounded extension may only push the instant later. RED (`C4`,
  renamed `A_clock_set_forward_past_the_instant_with_no_time_elapsed_ends_no_closure`): `after ONE forward jump : reopened=[loss_reopen:fake:SIM-001:2026-03-10]`,
  `clock suspect row : none`. Mutant (the monotone term dropped): the same. Second guard, the restart penalty: RED `A_restart_does_not_credit_the_gap_it_could_not_see`;
  mutant (the gap credited): the same line. Eight existing test clocks gained `GetTimestamp`/`TimestampFrequency` tracking their wall half — a fake that moves only the
  wall IS a moved machine clock, which is what the new class measures — with no assertion loosened.
- **The per-order limits are asked again at the wire,** inside the shared gate, for placements and approvals, on the reference price the record carries; the contract
  heading "Every gate is evaluated at the moment of dispatch, after the awaited reads" is now true. RED (`P2`, renamed `An_instrument_taken_off_the_allowlist_inside_
  the_gate_does_not_reach_the_wire`): `the order in flight : SENT`, `orders at the broker : [ES Buy 5]`. Mutant (the call inside the gate removed): `Expected:
  "RISK_LIMIT_EXCEEDED" Actual: "SENT"`. Not re-asked, by choice: the rate limit (advisory; the wire reservation bounds the minute), the PAPER-vs-account check
  (`ReauthorizeAtDispatchOrThrow` owns it), `quantity <= 0`; a price the intent NAMED skips the quote-age rule; a value cap switched on inside the window fails closed
  with `RISK_CHECK_UNAVAILABLE` off the cached instrument list rather than an awaited read inside the gate. `A_size_cap_narrowed_while_a_proposal_waited_refuses_the_
  approval` was GREEN on the base (`ApproveAsync` already ran the risk check inside the gate) — a guard checked in both directions, not a RED, stated as such.
- **The sighting is keyed by scope, the day by the confirming pull.** `Confirmed` files the first sighting under (connector, account, symbol); the record's `Day` is the
  CONFIRMING pull's instant at `Compose`, so a pair straddling midnight confirms and the day that lost the money is the day the record names. RED (`P4`, a `[Theory]`
  carrying the 22:59 control): `pull 2: reached=True closed=[]`, `breach rows : []`. Mutant (the day off the first pull): `Expected: "2026-03-11" Actual: "2026-03-10"`.
- `LossReopen.Step` added because `Hours` rendered a 90-second jump as "0 hours". All choices in `CONTRACTS.md` under `U-review-med`, landed with item 3's commit.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `9206207` (one line of `BUILD-STATUS.md` prose from the product tip `6116df7`,
rebased onto `b896e36`), Release `--no-incremental`: 0 warnings, 0 errors; Unit 1146 + Fault 374 + Integration 670 = 2190 passed, 0 failed, 1 skipped; eleven classes
touched or added 3× green (34 + 7); names `[Fact]`/`[Theory]` 1813 → 1821, eight added, 0 removed (plus `SetForward`, a test-clock helper the name pattern catches).
Manager's gate at `f9f5878` (the reported tip, 0 behind `main` `b896e36`; `src`/`tests` identical to the product tip `6116df7`), Release: build, 17 projects → 0 warnings, 0 errors; Unit 1146/1146 (21 s), Fault 374/374 (1 m 26 s), Integration 670/671, 1 skipped (11 m 6 s) → 0 failed; names vs `main` → 0 removed, 9 added (sets 1845 → 1854; `[Fact]`/`[Theory]` 1813 → 1821); scan clean; no trailers; `rev-list --count` → 0; CI at `f9f5878`: GREEN on all four jobs (run 35120414375: ubuntu, windows, macos, package).

**NOT done, NOT verified:** a fifth position gate; the review's UNVERIFIED list; anything on the bridge; the box; any defence of a closure against a clock moved while the
process is DOWN beyond declining to credit the gap — there is no trustworthy elapsed-time source across a process boundary, and the contract says so rather than
implying one; no box, no ATAS, no money.

## 2026-09-19 — the direction landed: `docs/PRINCIPLES.md` is the product definition, `manager-prompt.md` the fleet handoff, the read-gate points at both; docs only

The owner's corrective direction (draft 2026-09-17, prepared 2026-09-18, last pass 2026-09-19) landed as documentation on `90af8e4` — the handoff committed
untouched first, so the pass is a readable diff. No product file changed. `docs/PRINCIPLES.md` (106 lines) holds the principles verbatim from the handoff's § 3
with one grammar fix; the handoff (201 → 100 lines) points at it, marks this landing done, bounds the fleet's reading, tells the survey to be a general-purpose
leg (an Explore leg cannot write its report), and anchors every § 4 evidence bullet to file:line; `CLAUDE.md` "Start here" names the principles ahead of the
resume file with the preserved protections and the build-fleet/product-organisation distinction, and its test count is corrected (329 → ~2,190); `docs/COUNCIL.md`
carries a precedence note (its ten rules and boundary protections stay; its fixed organisation and unbuilt machinery are proposals to reassess, not a backlog);
the resume block's item 6 no longer lists `U-allocator-1`, `U-promote-bounds`, `U-flatten-2` and the milestone review as pending, decision (e) is recorded as
decided (option B, `U-reopen-1`/`-2`), and the "three routes" are replaced by the survey-then-briefs queue with the release and the box kept as the owner's calls.

**Source observations at `90af8e4` (by `git grep`; NOT executed checks, NOT runtime verification), each written into the handoff's § 4:** `GrantedWorkerTools.cs:49-54`
six tool names, `:74` `Writable = [CouncilRoles.OutDir, "trading"]`, no exec or spawn tool; `TradingGateway.cs:112` `public Core.Strategy.Referee Referee`, its doc
"no pipe op and no `trade` verb asks for a verdict"; `Referee.Verdict` (`Referee.cs:177`) named in `src/` only by two doc comments, called from four test files;
`RequestVerdict` called only at `Referee.cs:186`; `StrategyEvaluator` named in `src/` only by `Backtest.cs`, `StrategyInterpreter.cs` and itself; `IntentDecision.From(`
called only from `IntentDecisionTests.cs`; `TradingGateway.Allocate` (`:145`) called only from `DashboardView.cs:2248` ("allocated by the account owner");
`CouncilRelay.ReportLines = 20` (`:57`); `WorkspaceRevisions.Restore` (`:71`) and `Snapshot` (`:82`); `BoundaryBaselines` (`BoundaryStore.cs:229`) checked at
`:106-109`; `CouncilBoundaries` class (`BoundaryStore.cs:360`) with `Open`/`OpenRetirement`/`Assess`/`Release`, named in ten `src/` files; `Containment.Sandbox()`
(`Containment.cs:43`) `new(false, "NONE", …)`; `FakeBroker.BasePrice` (`FakeBroker.cs:62`). `[Fact]`/`[Theory]` in `tests/`: 1821 — the last gate ran 2190
(Unit 1146 + Fault 374 + Integration 670, 1 skipped), so the old "329 tests" in `CLAUDE.md` was three weeks stale.

**Verified by running:** `git rev-parse HEAD` → `bd7d9c2d6b9cfac74161abc4fc951d80a11b4ab8` before the pass (the handoff's inspected sha); `git worktree list` → the main
tree only; `git branch` → `main` plus the merged unit branches and the three probes branches; `git status -sb` → `## main...origin/main`, clean after the first
commit and its push (`push exit: 0`); caps measured with `wc -l`/python: `manager-prompt.md` 100, `docs/PRINCIPLES.md` 106, the resume block 45, this section
30; the secret scan on the staged diff as a gate both times (judged false positives excluded by name: "unrelated secrets", "secret scan", `CancellationToken`,
`IpcToken`, `string token`); no `Co-Authored-By` trailer on either commit.

**NOT done, NOT verified:** no product code, no build, no suite run — docs only; CI at `90af8e4`: GREEN on all four jobs (run 35433376334: ubuntu, windows, macos, package); CI at `cadc212`: GREEN on all four jobs (run 35433804915: ubuntu,
windows, macos, package) — both docs-only, both recorded here, none owed; no survey
leg dispatched, no brief written, `docs/briefs/` not created; no box, no ATAS, no money; nothing here marks a feature implemented, a test passed or a boundary
enforced — the handoff's § 4 items are starting points for the survey, not findings.

## 2026-09-19 — the survey landed: `docs/INSPECTION-2026-09-19.md`, the reachable loop at `222dcc9`, five tables, 87 lines; docs only

The first leg of the paper line, as `manager-prompt.md` § 4 orders: one fresh general-purpose Opus leg, read-only, on its own worktree (branch `survey`), from
the 39-line brief `docs/briefs/SURVEY.md` (landed `222dcc9`), writing the one file. Merge `50c40cc` (ff-only), then this record with the brief retired. No product
file changed. Leg cost: 138k tokens, 47 tool uses, 5 m 47 s. Every cell labelled SOURCE / RUN / HIST; the leg built nothing, ran no test class, no CLI, no provider,
no box — every finding is a source observation or a `git grep`, and the report says so.

- **Earliest broken dependency (tables 3, 5):** candidate → verdict is ABSENT. `Referee.Verdict` (`Referee.cs:177`) has zero production callers (RUN `git grep -c
  'referee.Verdict(' -- src/` → 0; four test files), no pipe op and no `trade` verb asks for one, so promotion, allocation and everything downstream are unreachable
  ahead of the missing runner. Downstream, two more absences: no deployment store and no runner (`IntentDecision.From` has no `src/` caller; `StrategyEvaluator.Step`
  has one, `Backtest.cs:460`; the gateway's own doc at `TradingGateway.cs:1856-1858` says "no runner emits a live or paper intent yet"), and no advancing price
  source (Binance completed-month archives only, `BinanceArchive.cs:16-21`; `FakeBroker.BasePrice` deterministic and unmoving, `FakeBroker.cs:62-67`).
- **Connected and preserved (tables 1, 3):** turn → data read, program creation (`write_file` into `trading/`), backtest (`Ops.Backtest` for every role; version and
  run recorded; a trial registered when a campaign is open), fills → `pnl`/`report` for orders that exist, the next turn scheduled from `next.json`; the holdout door
  (`BarAudience.Referee`, internal), the charge-first referee, the allocation ceiling on dispatch, the boundaries' deadlines and code-written defaults.
- **Obstructions (table 2):** the verdict op (smallest correction: a read-only op calling the existing `Referee.Verdict` under the caller's role, returning
  `RefereeFeedback.Text`, the charge and the holdout untouched); the campaign (opened only by the owner's holdout press, `SettingsView.cs:476`, in one transaction with
  the cutoff); paper allocation (`Allocate` has one caller, the owner's press, `DashboardView.cs:2248`); no exec and no spawn tool in the harness. Preserved as
  reasonable: the 20-line report cap with its quarantine, the assessment baseline, owner-only data collection.
- **Boundaries (table 4):** operator authority off the pipe by absence (24 op names, none for mode, kill switch, approval, allocation, verdict or update); an agent's
  `trade buy` today names no version, so it is un-ceilinged and gated by mode, role and the kill switch instead; `Containment.Sandbox()` NONE — stated, not enforced;
  the Operations role's vendor CLI runs `--dangerously-bypass-approvals-and-sandbox` (`RuntimeManifest.cs:461`) while the harness role has six tools and no exec —
  an asymmetry recorded, not judged.
- **Queue (table 5), by dependency:** `U-verdict-op` → (`U-campaign-open`, likely nothing: the campaign opens with the owner's one-time cutoff) → `U-paper-grant` →
  `U-paper-runner` → `U-advancing-bars` → `U-decide-again`. Owner-only prerequisites named: the 12-month download, one holdout press, a harness key.

**Manager's checks after the report (RUN, this Mac, 2026-09-19):** the six load-bearing claims re-read at the cited lines — all hold. One clause the survey did not
name, which shapes the first brief: `ScoringPolicyV1.Reason` (`Referee.cs:475`) answers `evidence-precedes-the-freeze` whenever the holdout window's start is not
AFTER the version's `created_at`, so over completed-month archives no version frozen today can be promoted before a later month is collected — the principles'
"favourable historical verdict → eligible for paper" distinction is not in the code. Binance's public klines answer this Mac without a key: `GET
api.binance.com/api/v3/klines?symbol=BTCUSDT&interval=1m&limit=2` → HTTP 200 in 0.46 s, the current minute's bar; `data-api.binance.vision` → HTTP 200 too.

**Verified by running:** the survey worktree clean at `50c40cc`, one file vs `main`; scan clean; `merge --ff-only` exit 0; `rev-list --count` → 0; caps measured
(`wc -l`: report 87, brief 39, this section); no trailers. CI at `222dcc9` (the brief), at `50c40cc` and at this record: PENDING at the time of writing, recorded when complete.

**NOT done, NOT verified:** no build, no suite, no runtime — nothing in the report or in this section marks a feature implemented, a test passed or a boundary
enforced; the Binance probe is a read of public data from this Mac and proves nothing about the product; no Astra consult yet; no brief for a product unit yet.

## 2026-09-19 — U-verdict-op landed: the candidate → verdict arrow is connected — a council role asks the app's referee for a verdict on its own frozen version, bounded, idempotent and sanitised

The first paper-line unit, the survey's earliest broken dependency (`docs/INSPECTION-2026-09-19.md` tables 3 and 5), built by one fresh Opus builder from the
39-line brief `docs/briefs/U-verdict-op.md` (landed `a617486`), resumed once after a wifi outage stalled it at its first step with nothing on disk. Merge
`413c6de`, 5 commits (4 items + the report), 14 files, +939/−26, no schema rung. NOT the money path: nothing here reaches `PlaceAsync`. The sibling's five files
untouched (`git diff --name-only` → 0 of them). Preceded by the one bounded Astra consult `manager-prompt.md` § 7 allows (2026-09-19, `gpt-6-astra`, "high",
≈ 7 min, answer file only, sent to the owner, kept out of the repo): its advice taken into this brief — admission needs a COMPLETED research run by the asking
role, the request is idempotent so a dataset that grew buys no second peek — and into the queue: a typed paper-eligible verdict, one paper envelope, forward
bars, a paper adapter, a deployment record, the runner, then the observed loop; its one pre-run insistence, a crash-after-acceptance recovery test.

- **`Ops.Verdict`** (`Protocol.cs`), a read for the gateway — not in `Mutating`, a zero connector path (`HandlerPaths`, `TimeSpan.Zero` like `Backtest`). It asks for
  one of the final judgements a campaign budgets; it reads no bar, moves no cutoff, opens and renews no campaign.
- **`VerdictFor`** beside `BacktestFor` (`GatewayPipeServer.cs`): a known council role (`Backtests.RoleOf`, made `internal`, its refusal unchanged — the operator and
  a roleless connection get no verdict); an OPEN campaign for the dataset (`Campaigns.OpenForDataset`; none → the owner's holdout press named); a COMPLETED
  registered run of that version by that role (new read `StrategyStore.CompletedRunsOf`; `--dataset` optional when exactly one qualifies); one verdict at a time
  per role; IDEMPOTENT on (version, campaign) — an existing promotion is answered as it stands and the referee is not called. Otherwise the EXISTING
  `Referee.Verdict` runs unchanged: charge, audience, promotion row, delivery to Research and boundary as before. The reply carries the verdict, the reason
  class, `text` = `RefereeFeedback.Text` byte for byte, spent/budget — no metric, trace hash, run figure or bar.
- **`trade verdict --version <hash> [--dataset <id>]`**, `Ops.Verdict` in `GrantedWorkerTools.TradeOps` for every role, described in `GatewaySchema` and in two
  lines of the mission instructions; `CONTRACTS.md` § campaign now says the verdict REQUEST is an op and the holdout, campaign, trial and disposition still have
  none — the per-lineage verdict budget and the sanitised text are what bound "searching the holdout by asking"; one `USER-GUIDE.md` sentence.
- **Deviations, kept:** `text` asserted equal to `RefereeFeedback.Text(promotion)` rather than containing the owner's report line (the brief conflated the two);
  `RefereeVerdictTests.No_pipe_op_asks_for_a_verdict_or_writes_a_promotion` now asserts exactly ONE op named verdict, non-mutating, args {version, dataset}, with the
  promote/referee/holdout/campaign/trial/disposition bans intact and its name kept — the one existing assertion changed; the builder overwrote the pre-existing
  `VerdictOverPipeTests.cs`, noticed, restored it verbatim and rebuilt the three commits (name diff: 0 removed).

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `3f3edf1` (on `a617486`), `dotnet clean` then Release `--no-incremental`:
17 projects, 0 warnings, 0 errors; Unit 1146 + Fault 374 + Integration 677 = 2197 passed, 0 failed, 1 skipped (the `[Fact(Skip)]` already on `main`); touched classes
3× → 19/19 Integration, 12/12 Unit each pass; names 1855 → 1861, 6 added, 0 removed. RED: `{"code":"INVALID_REQUEST","message":"unknown operation 'verdict'"}` on 4
of the 6 new tests. Mutant (`text` built from the holdout run's metrics): `Assert.DoesNotContain() Failure: Sub-string found  String: ···"48, gross 48, trace
802c9c601482cc1e6e26e"···` at `VerdictOverPipeTests.cs:279`; reverted, 6/6 green. Manager's gate at `3f3edf1` (the reported tip; landed as `413c6de` after a docs-only rebase over `d123e91`, `src`/`tests` identical), Release: build `--no-incremental` → 0 warnings, 0 errors; Unit 1146/1146 (21 s), Fault 374/374 (1 m 25 s), Integration 677/678, 1 skipped (11 m 3 s, its normal length) → 0 failed. Names vs `main` (from git objects, `[[:space:]]`): sets 1854 → 1860,
0 removed, 6 added; `[Fact]`/`[Theory]` 1821 → 1827. Scan: two judged false positives excluded by name in that one command (a doc comment "token on every frame";
`grants.Issue(...).Token`, a property read), otherwise clean; no trailers; `rev-list --count` → 0. CI at `413c6de`: recorded when complete. Docs-only shas recorded: `222dcc9` (run 35440880279),
`50c40cc` (35441312812), `c7b22a3` (35441352574), `a617486` (35441665140) — each GREEN on all four jobs (ubuntu, windows, macos, package).

**NOT done, NOT verified:** no test of the one-at-a-time refusal (two concurrent pipe callers); `RefereeFeedback.Text` still ends "You cannot ask for one" — stale,
in `Referee.cs`, which `U-paper-verdict` owns and was told to fix; no real model has asked for a verdict — every caller is a test over the pipe against the
simulator; over completed-month archives every production verdict is still `evidence-precedes-the-freeze` until `U-paper-verdict` lands; no box, no ATAS, no money.

## 2026-09-19 — U-paper-verdict landed: a favourable verdict over never-served HISTORICAL holdout months makes a version eligible for PAPER, and the live path refuses it categorically

The principles' distinction "a favourable historical verdict → eligibility for paper observation" put into the referee (`docs/PRINCIPLES.md` § Evidence), built by
one fresh Opus builder from the 38-line brief `docs/briefs/U-paper-verdict.md` (landed `a617486`), resumed once after the wifi outage with nothing on disk, given
one extra item mid-leg by the manager. Merge `d8000fa`, 7 commits (5 items + one test move + the report), 14 files, +996/−42. **Schema 23.** MONEY PATH:
`Allocations.Record`'s standing check — a refusal added, none removed.

- **A second standard, fixed at open.** `CampaignPolicy.PaperV1` — V1's performance clauses (run completed, ≥ 1 closed trade, net after declared costs > 0)
  without the freeze clause, worded "historical holdout evidence: eligible for paper observation only" — with its own sha; `campaign.paper_policy` and
  `paper_policy_sha256` copied at `Open`, carried by `Renew`; the rung pins this build's PaperV1 onto every campaign that predates it (the one backfill this rung
  makes, `CONTRACTS.md` says so); a campaign that fixed ANOTHER paper policy is not judged by this build's clauses.
- **One holdout run, two readings.** `ScoringPolicyV1.Reason` is asked first and is unchanged; only when its answer is `evidence-precedes-the-freeze` are the paper
  clauses ACTUALLY evaluated on the same run: all met → `paper-eligible` (`MetOnHistory`, its own reason class), else `refused` with the performance clause that
  failed — the informative reason, never the freeze. The row's policy sha is PaperV1's for a paper-eligible verdict and the campaign must hold it.
- **A fifth standing, `paper_eligible`,** invalidated by exactly what invalidates a promotion; `IsPromoted` stays FALSE for it; `IsPaperEligible` added; the live
  allocation path refuses it naming "historical evidence; paper only"; the dispatch gate untouched (no allocation → `ALLOCATION_NONE` as before).
  `BoundaryBaselines.IsKnown` widened to accept the new state (its own `Measure` returns the standing) — no guard weakened.
- **Delivered, no ceremony.** A paper-eligible verdict wakes Research with the sanitised note and opens NO consequential boundary — paper observation is an ordinary
  experiment by app policy; the directors' 24 h review stays the ceremony of `promoted`. `RefereeFeedback.Text` says PAPER-ELIGIBLE and why it is not promoted, and
  its stale "You cannot ask for one" sentence (the manager's added item) now names `trade verdict` and the budget across renewals — no digits, the no-figure
  test unchanged and exact. `PromotedLine`, report section 8 (asserted on the rendered document), `CONTRACTS.md`, `USER-GUIDE.md`.
- **Kept, stated:** one existing assertion changed — `RefereeVerdictTests.A_version_frozen_after_the_held_back_window_begins_is_refused_in_words`, name and fixture
  kept, its recorded word now `PaperEligible`/`MetOnHistory`, its original property asserted unchanged. `Promotions.Current()` not widened (not asked).

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `d71e9ad` (the last code commit, rebased onto `3557e1e` which carried
`U-verdict-op`, no conflicts), Release `--no-incremental`: 17 projects, 0 warnings, 0 errors; Unit 1156 + Fault 374 + Integration 677 = 2207 passed, 0 failed,
1 skipped; touched classes 3× → 30 each; names 1861 → 1871, 0 removed. RED on the base, 5 of 6: `Expected: "paper-eligible" / Actual: "refused"`; `Expected:
"not-profitable-after-costs" / Actual: "evidence-precedes-the-freeze"`; `Expected: "paper_eligible" / Actual: "refused"`;
`A_version_frozen_before_the_cutoff_is_still_promoted_under_V1` GREEN on the base — a guard checked, not a RED. Mutant (i), `IsPromoted` true for the new state:
`capital was allocated to a paper-eligible version`; mutant (ii), the paper clauses skipped: `Expected: "refused" / Actual: "paper-eligible"`; both put back.
Manager's gate at `d8000fa` (the reported tip, 0 behind `main` `3557e1e`; `src`/`tests` identical to the gated `d71e9ad`), Release: build `--no-incremental`, 17 projects → 0 warnings, 0 errors; Unit 1156/1156 (22 s), Fault 374/374 (1 m 27 s), Integration 677/678, 1 skipped (11 m 3 s, its normal length) → 0 failed. Names vs `main` (git objects): sets 1860 → 1870, 0 removed, 10 added; `[Fact]`/`[Theory]` 1827 → 1837. Scan clean; no trailers; `rev-list
--count` → 0. CI at `d8000fa`: recorded when complete.

**NOT done, NOT verified:** nothing runs a paper-eligible version forward on paper — no envelope, no paper allocation, no runner (`U-paper-envelope` cut from this
tip); `MissionLoop.PromotedLine`'s `_` arm still says "You cannot ask for a verdict" (handed to the envelope builder); `DailyReports.cs` unchanged beyond what
section 8 already printed; no real model has received a paper-eligible verdict; no box, no ATAS, no money.

## 2026-09-19 — U-forward-bars landed: real, advancing one-minute bars the app collects itself — closed bars only, receipt provenance, first reading stands, apart from the frozen evaluation datasets

The data arrow of forward paper (`manager-prompt.md` § 5 "Actual market observation"), built by one fresh Opus builder from the 36-line brief
`docs/briefs/U-forward-bars.md` (landed `d123e91`) as the second of three parallel legs the owner allowed. Merge `6016fdd`, 5 commits (4 items + the report),
26 files, +2767/−19. **Schema 24** (over `U-paper-verdict`'s 23). Not the money path: nothing here places an order, and staleness adds no dispatch gate —
`data_freshness` is still `RefuseAStaleDecisionOrThrow` against the decision's own bar.

- **The forward ledger** (`ForwardBarStore`): `forward_bar` keyed (source, symbol, open_time), `forward_fetch` (every attempt, succeeded or failed, with its
  body sha), `forward_gap` (recorded, never filled). A bar is written only when its close precedes its receipt; a re-fetched bar that differs is refused by the
  insert's own conflict clause — the first reading stands and the disagreement is counted on the fetch. `Since` clamps at `MaxBars + 1` so a window over the cap
  is REFUSED naming the cap, never truncated (a defect the builder found in its own first cut, stated).
- **`ForwardBarCollector`** (Provisioning): every 60 s for the market-data pair while the Data page's one-press toggle (default ON) allows; the host is a
  catalogue row `binance-spot-forward-klines` on `data-api.binance.vision`, the market-data-only host, `Verified = true` with the vendor's document on the row —
  data, not code; 10 s request timeout, exponential backoff to 5 min, a `BarClosed` event for the runner to come; started and stopped by `AppHost` with the app,
  independent of the mission loop. RUN 2026-09-19 from this Mac (the manager, before the brief): the endpoint answered HTTP 200 in 0.46 s with the current
  minute's bar, no key; the builder did NOT re-run it and quotes the brief.
- **The read-only surface:** `data-list` names the forward series apart from the frozen datasets with the sentence "FORWARD — collected by TradeAgent minute by
  minute; no vendor checksum; not evaluation evidence"; `data-bars --source forward` serves a bounded window to any role with no holdout (every forward bar
  post-dates every freeze, `CONTRACTS.md` says so); `status.forward_data` (symbol, last bar, age, gaps today, last error); report section 7 prints a COUNT, never a
  price, the existing "nothing here is priced" gap kept. `CONTRACTS.md` "Forward bars", `USER-GUIDE.md` Market data, `RESEARCH-REQUIRED.md` C5 (host and endpoint
  with source and date).
- **Kept, stated:** one existing assertion changed — `DataOpsTests.The_schema_names_both_data_operations_and_neither_of_them_mutates` pins the usage string with
  `[--source archive|forward]` and asserts `source` optional; `SuiteReachesNoVendorTests` strengthened twice (the forward host `data-api.binance.vision` does not
  contain `data.binance.vision`, so the old scan missed it; a test building a collector must name a `baseUrl`, proved to fire). Item 1's line said "Schema 25", a
  typo; 24 was built.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate first at `3331fc0` (over `3557e1e`, ladder 22 → 24 with no 23 block, as
briefed: Unit 1158 + Fault 374 + Integration 685, 0 failed), then at `c7a2840` after the rebase over rung 23 (`main` `e89a4cb`; `Database.cs` and `Versioning.cs`
conflicted, both sides kept in ladder order, the rollback fixture carrying both arms; `git diff 3331fc0 c7a2840 -- src tests` = rung 23 and nothing else), Release
`--no-incremental`: 0 warnings, 0 errors; Unit 1168 + Fault 374 + Integration 685 = 2227 passed, 0 failed, 1 skipped (11 m 7 s); touched classes 3× → five Unit
classes 34 ×3, `ForwardBarsOverPipeTests` 8 ×3, every class asserting `schema_version` 109 ×3; names 1871 → 1889, 18 added, 0 removed. RED (behavioural): the integration class `Failed: 7, Passed: 0`, the
headline refusing `{"code":"HOLDOUT_WITHHELD","message":"dataset 1 (BTCUSDT 1m v1) holds out every bar from 2026-08-01 01:00:00Z onwards…"}` — `--source`
ignored, the archive answered; the rollback fixture `SqliteException : SQLite Error 1: 'no such table: forward_bar'` at `VenueCatalogTests.cs:246`; the rest red
on the base only as `CS0246` (the weak red it is, stated). Mutant (i), the closed-bar check dropped: `Only_closed_bars_are_stored… Expected: 2 Actual: 3` at
`ForwardBarStoreTests.cs:59`. Mutant (ii), first-reading-stands replaced: `A_differing_refetch… Expected: 0 Actual: 2` at `:167` — it PASSED at first because
`Append` read-then-continued before the insert; fixed so the conflict clause refuses. Manager's gate at `c7a2840` (the reported tip; landed as `6016fdd` after a docs-only rebase over `636ea94`, `src`/`tests` identical), Release: build `--no-incremental`, 17 projects → 0 warnings, 0 errors; Unit 1168/1168 (22 s), Fault 374/374 (1 m 27 s), Integration 685/686, 1 skipped (11 m 8 s, its normal length, beside two builders' suites) → 0 failed. Names vs `main` (git objects): sets 1870 → 1888, 0 removed, 18 added. Scan: one judged false positive excluded by name ("no passwords", a contract sentence), otherwise clean; no trailers; `rev-list --count` → 0. CI at `6016fdd`: recorded when complete.

**NOT done, NOT verified:** the Market data card never rendered (`tools/mac-run.sh` not run); no run against the real host by the builder, no vendor call from any
test (`SuiteReachesNoVendorTests` proves it); no order anywhere; nothing consumes the bars yet — no runner, no paper adapter wired to `IPaperBarSource`; no
box, no ATAS, no money.

## 2026-09-19 — U-paper-adapter landed: the app's own PAPER connector — real bars in, simulated fills out at the next open, a persistent idempotent book, no way to reach a venue

The execution model of forward paper (`manager-prompt.md` § 5 "Actual market observation"; Astra 2026-09-19: "the adapter must possess only simulated execution
capability"), built by one fresh Opus builder from the 40-line brief `docs/briefs/U-paper-adapter.md` (landed `3557e1e`) as the third parallel leg the owner
allowed. Merge `1616de7`, 5 commits (4 items + the report), 23 files, +2262/−20, two new projects (`Connectors.Paper`, `Platforms`). No app schema rung: the book is the connector's own SQLite file. Not the live money path: the connector references no HTTP
client and no venue SDK, and a test reflects over the assembly to say so.

- **`TradeAgent.Connectors.Paper`** in the solution, referenced by the App and the GatewayHost as Fake is: `Id = "paper"`, `IsPaper`, `SupportsClientOrderId`,
  `SupportsOrderHistory`, `SupportsModify`, `SupportsClosePosition` true (the first two TRUE because tests prove the round-trip and the reach-back — rules 1 and
  2 of `CLAUDE.md`, literally), `SupportsStreaming` false; one simulated account `PAPER-1` (USDT, 10,000 declared at creation, then owned by the file);
  instruments = the catalogue's VERIFIED non-simulator rows with their increments (the shipped catalogue → an empty list and a placement refused naming
  `venues.json`); an injectable clock; `IPaperBarSource` with a `MemoryBarSource` for tests.
- **The book, persistent and idempotent:** `state/paper-<account>.db`, `paper_meta.schema = 1` inside the file (a newer file is refused, not read); orders keyed by
  client order id; `paper_fill UNIQUE (client_order_id, bar_open_time)`; average-cost positions; decimals as invariant text; every SDK read off the file, equity
  = starting + realised − fees, unrealised off the last close and NULL with none.
- **Settlement in the backtest's order:** a market order fills at the next CLOSED bar's open plus adverse slippage; a stop at that open on a gap-through, else at
  its level; a limit at its level; a stop firing takes the bar from every limit on that instrument; the declared fee on every fill; sizes rounded DOWN;
  FRICTIONLESS written on the fill row and in the status detail when no fee and no slippage are declared (`PaperFeeFraction`/`PaperSlippageFraction`, default 0).
  `ConnectorRejectedException` only for an unverified symbol, a size below the increment, a modify or cancel of a non-WORKING order; a throwing bar source or
  book I/O propagates (rule 3).
- **`Platforms.Connectors.Create(id)`** replaces both hard-coded ternaries (`fake` | `atas` | `paper`) — a new one-file project `TradeAgent.Platforms`, because
  the Gateway must stay unable to name a concrete connector (`PlaceRouteTests`) and both hosts need the factory; the picker offers "TradeAgent paper — real
  prices, simulated fills" in one press. `CONTRACTS.md` "The paper connector"; `USER-GUIDE.md` one section. Deviation stated: the four commits split by test
  file, the first carrying all three items' source, because the connector and its book do not compile in halves.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `01559f0` (rebased onto `636ea94`), Release `--no-incremental`: 19
projects, 0 warnings, 0 errors; Unit 1156 + Fault 384 + Integration 678 = 2218 passed, 0 failed, 1 skipped (`PipeContractTests`'s `[Fact(Skip)]`, on `main`
too); touched classes 3× → three Fault classes 10/10 each, `PaperGatewayTests` 1/1; names 1871 → 1882, 11 added, 0 removed. RED on the base (greenfield):
`error CS0234: … 'Paper' does not exist in the namespace 'TradeAgent.Connectors'`. Mutant (i), fill at the placing bar's close: `Assert.Equal() Failure …
Expected: 110.110 / Actual: 105.105`. Mutant (ii), the fills' UNIQUE key dropped: `Assert.Single() Failure: The collection contained 3 items` — it SURVIVED
the first attempt because the execution id's PRIMARY KEY enforced the same fact; the id became a sequence so the named key is the only guard, then red.
Rebased once over `U-forward-bars` (one conflict, `Trading.cs`: both units' settings kept, market data first, 0 deletions either side; `git diff 01559f0 1616de7 -- src tests` = forward-bars' own files and nothing else) and re-gated at `1616de7` on `f4d271b`, each suite alone: Unit 1168 + Fault 384 + Integration 686 = 2238 passed, 0 failed, 1 skipped; names 1889 → 1900, 11 added, 0 removed. Manager's gate at `1616de7` (the reported tip, 0 behind `main` `f4d271b`), Release: build `--no-incremental`, 19 projects → 0 warnings, 0 errors; Unit 1168/1168 (22 s); then CONTAMINATED — the rate-limit stoppage of 2026-09-19 evening left the detached gate running through the night beside another builder's suite: Fault 383/384 in 2 h 6 m, Integration 513/687 with 173 red in 11 h, the reds in the pipe and deadline `Timing` classes; re-run ALONE on the same build 2026-09-20 morning (`--no-build`): Integration 686/687, 1 skipped (11 m 5 s, its normal length), Fault 384/384 (1 m 28 s) → 0 failed, both runs quoted. Names vs `main` (git objects): sets 1888 → 1899, 0 removed, 11 added. Scan clean; no trailers; `rev-list --count` → 0. CI at `1616de7`: recorded when complete. Recorded now, GREEN on all four jobs each: `d8000fa` (run 35459469327), `e89a4cb` (35459470564), `636ea94` (35459703717), `6016fdd` (35460941975), `b715648` (35460943424), `f4d271b` (35461077856).

**NOT done, NOT verified:** no live bar source is wired — `Connectors.Create` hands the paper connector an EMPTY `MemoryBarSource` until the runner unit binds
`ForwardBarStore` to `IPaperBarSource`, so in the app it quotes nothing, fills nothing and says so; "a live mode refuses its account" is the installation's
EXISTING `LIVE_NOT_ACTIVATED` gate quoted in the test — NO new gate was added (one refusing a simulated account in a live mode would redden every live-mode
test against the simulator); the picker never rendered; no box, no ATAS, no venue, no real order.

## 2026-09-20 — U-paper-envelope landed: the owner grants bounded paper experimentation ONCE, and eligible versions are allocated to paper by app policy — never to live

The paper-authority arrow (`manager-prompt.md` § 5 "Paper authority"; `docs/PRINCIPLES.md` § boundary: live allocations keep their deliberate owner confirmation),
built by one fresh Opus builder from the 36-line brief `docs/briefs/U-paper-envelope.md` (landed `d123e91`), cut from the paper-verdict tip, stalled once by the
rate-limit stoppage mid-gate and resumed with one message, rebased over forward-bars and the adapter. Merge `add2671`, 5 commits (4 items + the report), 24 files, +2447/−64. **Schema 25.** MONEY PATH:
`AllocationCeilingOrThrow` inside dispatch and every reader of `strategy_allocation`.

- **`paper_envelope`:** one immutable row per grant (connector, account, symbol, currency, ceilings, max_deployments, granted_at, expires_at, reason; `withdrawn_at`
  write-once), written ONLY by a two-press Dashboard card, refused unless BOTH witnesses call the account simulated (`AccountInfo.IsSimulated` AND
  `Capabilities.IsPaper`, stricter than the PAPER-mode check's `||`) and the mode is PAPER at the press; withdrawal one press plus confirm; `Envelopes.Standing`.
- **A scoped allocation:** five nullable columns on `strategy_allocation` (`scope`, connector, mode, account, envelope), NULL read as LIVE — an owner's press is never
  a paper grant; a paper row's id hashes the seven facts PLUS the scope facts (`PaperIdOf`; live ids unchanged); `StandingForLive` / `StandingForPaper` /
  `PaperStanding` / `InEnvelope`; `RecordPaper` refuses unless the standing is `paper_eligible` or `promoted`, the envelope stands, the ceiling fits and the
  envelope has room; the live press refuses a paper scope and still asks `IsPromoted` alone.
- **Dispatch splits on the gateway's own (connector, mode, account):** a paper row authorises nothing in `LIVE_*`, categorically; on an envelope's account in PAPER
  only a paper row matching the pair authorises; an AGENT order naming no version on an envelope account is `ENVELOPE_ACCOUNT_RESERVED` (the owner's press
  unaffected). **Deviation kept, stated in `CONTRACTS.md`:** off an envelope account, PAPER mode still reads LIVE rows — the literal brief would have removed the
  owner's ceiling from every practice order and reddened eight existing money-path tests; the protected property (paper never authorises live) holds either way.
- **App policy:** `AllocatePaperDue` on the `ApplyBoundaryDeadlines` seam and after each verdict delivery writes the paper allocation at the envelope's ceiling with
  one persisted note-and-wake to Research keyed by the allocation; report section 4 lists paper allocations apart ("PAPER — no live authority"); `PromotedLine`
  gained the paper clause and (the manager's added item) its stale `_` arm now names `trade verdict`, the budget, and a verdict-and-reason-class-only reply.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `56eac76` (rebased onto `78394a2`; rebase conflicts: forward-bars in
`Database.cs`/`Versioning.cs` only, ladder 23 → 24 → 25 with the constant at 25; the adapter none; `git diff e006439 56eac76 -- src tests` = the adapter's own
files and nothing else), Release `--no-incremental`: 0 warnings, 0 errors; Unit 1177 + Fault 393 + Integration 686 = 2256 passed, 0 failed, 1 skipped (11 m 6 s);
touched classes 3× → Unit 44, Fault 23; names 1900 → 1914, 0 removed. RED: `error CS1061: 'TradingGateway' does not contain a definition for
'GrantPaperEnvelopeAsync'` / `'RecordPaper'` / `'AllocatePaperDue'`; (e) `Expected start: "ok" / while it stood: ALLOCATION_NONE — strategy version 6b52acd9f0d5 has no
capital allocated…`; (g) `Expected start: "ENVELOPE_ACCOUNT_RESERVED" / agent, envelope: ok — FILLED`; (d) GREEN on the base — a guard checked, not a RED. Mutant (i),
live rows read in PAPER mode: `A_paper_allocation_never_authorises_a_live_dispatch: Expected start: "ALLOCATION_NONE" / outcome: ok — FILLED` and `another platform:
ok — FILLED / another account: ok — FILLED`; mutant (ii), the scope facts dropped from the id: `Assert.NotEqual() Failure: Strings are equal / first id
10174625b9d8… / second id 10174625b9d8…`; both put back. Manager's gate at `71dd3a9` (the reported tip; landed as `add2671` after a docs-only rebase over `1031fcd`, `src`/`tests` identical), Release: build `--no-incremental`, 19 projects → 0 warnings, 0 errors; Unit 1177/1177 (28 s), Fault 393/393 (1 m 36 s), Integration 686/687, 1 skipped (11 m 6 s, its normal length, beside one builder's work) → 0 failed. Names vs `main` (git objects): sets 1899 → 1913, 0 removed, 14 added. Scan clean; no trailers; `rev-list --count` → 0. CI at `add2671`: recorded when complete.

**NOT done, NOT verified:** nothing dispatches a paper allocation — no deployment, no runner (`U-deployment` cut from this tip); the envelope card never rendered;
no real model has been allocated to paper; no box, no ATAS, no venue, no real order.

## 2026-09-20 — U-language-in-home landed: the strategy language and its three worked programs reach every Research home, app-owned, regenerated every start

The obstruction the observed-run survey found (read-only, 2026-09-20 morning: nothing in `src/` put the grammar or an example program where a Research turn
could read it — `AGENTS.md` named `trade backtest` once, `trade schema` described arguments only, the three day-one programs were test fixtures), built by one
fresh Opus builder from the 24-line brief `docs/briefs/U-language-in-home.md` (landed `1031fcd`) as the second leg beside the envelope. Merge `ed3b224`, 4
commits (3 items + the report). No schema rung. Not the money path.

- **The reference ships:** `docs/STRATEGY-LANGUAGE.md` is `Content` of `TradeAgent.AgentRuntime` (present after `dotnet publish -r win-x64 --self-contained`, what
  the installer stages); `ResearchLibrary.Write`, from `WorkspaceBuilder.Build`, writes `<roleHome>/research/STRATEGY-LANGUAGE.md` on every start, body IDENTICAL
  to the doc (`diff`), the first line stating app ownership. One source of the grammar; no copy in code.
- **The three programs ship, and move:** `tests/TradeAgent.UnitTests/Strategies/*.strategy` → `src/TradeAgent.AgentRuntime/Strategies/*.strategy` (`git mv`,
  recorded as renames), written to `<roleHome>/strategies/examples/` byte for byte on every start; the parser tests read the shipped files through
  `tests/Shared/DayOnePrograms.cs`, whose name list IS `ResearchLibrary.Programs`; no app-owned banner inside a program (byte-for-byte wins), ownership stated
  in the reference's first line, `AGENTS.md` and `trade schema` instead — a deviation stated.
- **The role is told:** three lines in `AGENTS.md` (the reference, the examples, `trade backtest --strategy strategies/examples/ma-crossover.strategy --dataset
  <id>` as the first thing to run) and the reference path in `GatewaySchema`'s backtest entry, all interpolated from `ResearchLibrary`; one guard beyond the
  brief (`The_mission_and_the_schema_name_the_reference_the_examples_and_the_first_backtest`, RED first).

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `df94d08` (the product tip, rebased onto `c17d5c1`, no conflict; the
report commit `d28d7da` docs-only), Release `--no-incremental`: 19 projects, 0 warnings, 0 errors; Unit 1181 + Fault 393 + Integration 687 = 2261 passed, 0 failed,
1 skipped (11 m 8 s); touched classes 3× → 55 Unit, 11 `BacktestOverPipeTests`; names 1914 → 1919, 0 removed. RED on the base: `the Research home has no language
reference at …/research/STRATEGY-LANGUAGE.md`; `Expected: ["src/TradeAgent.AgentRuntime/Strategies/ma-crossove"···] Actual: ["tests/TradeAgent.UnitTests/
Strategies/ma-crossover"···]`; `the Research home has no worked program at …/strategies/examples/ma-crossover.strategy`. Mutant (the examples written once, never
refreshed): `Assert.Equal() Failure: Collections differ` at `ResearchLibraryTests.cs:106`; put back. Manager's gate at `d28d7da` (the reported tip; landed as `ed3b224` after a docs-only rebase over `d9ae716`, `src`/`tests` identical), Release: build `--no-incremental`, 19 projects → 0 warnings, 0 errors; Unit 1181/1181 (24 s); Integration 687/688, 1 skipped (11 m 7 s, its normal length); the Fault run of that gate wrote NO output (a one-line log holding only its exit code 0, finished in the same second as Unit) and is not counted — Fault re-run alone on the same build: 393/393 (1 m 27 s) → 0 failed. Names vs `main` (git objects):
sets 1913 → 1918, 0 removed, 5 added. Scan clean; no trailers; `rev-list --count` → 0. CI at `ed3b224`: recorded when complete.

**NOT done, NOT verified:** no real model has read the reference; the box, the installer's actual staging on Windows (the publish was run on this Mac); noted
and NOT fixed, a one-item follow-up: `MaterialScanner` walks `research/` and `strategies/`, so these app-written files record as `MaterialOrigin.Agent` — the
reading it already applies to the app-owned `in/`.

## 2026-09-20 — U-deployment landed: a paper deployment is an app-owned record with lineage, an execution identity the pipe cannot mint, a bar cursor and write-ahead operations

The paper allocation → deployment arrow (`manager-prompt.md` § 5 "Forward execution"), built by one fresh Opus builder from the 38-line brief
`docs/briefs/U-deployment.md` (landed `636ea94`), cut from the envelope tip, rebased three times as `main` moved, no conflicts. Merge `dae8605`, 5 commits (4 items + the report), 24 files, +2326/−15.
**Schema 26.** MONEY PATH: a new in-process caller of `PlaceAsync` and `CloseAsync`, PAPER only.

- **The record:** `strategy_deployment` — seven identity facts hashed into the id, immutable; `state` (`active` | `suspended` | `ended`), `cursor_open_time`,
  reasons and instants written only by `Start/Suspend/Resume/End`, one guarded UPDATE each; `deployment_op` keyed by the sendable `dp-<12>-<bar>-<seq>` request
  id with kind, the intent as JSON, `planned → dispatched → resolved | refused`, and the answer. Both rollback fixtures drop 26.
- **The identity:** `AgentContext.Deployment(id)` — `ForAgent` cannot build one, the pipe refuses the reserved name and serves none of its rows; refused
  `MODE_FORBIDS_EXECUTION` in both live modes at authorisation AND re-asked at dispatch; under it `PlaceAsync` runs every existing gate unchanged. RED with the
  live guard removed, quoted: the order was PARKED for approval (`"request dp-live-1 is waiting for your app"`) rather than refused — the guard is what refuses.
- **Policy and lifecycle, app-owned (gateway methods, the ledger class owning the four transitions — a deviation stated):** `StartPaperDeploymentsDue` on the
  mission seam after `AllocatePaperDue` (one deployment per standing paper allocation while the envelope has room); `ReconcilePaperDeploymentsAsync` in the
  app's and the gateway host's loops (a `dispatched` op takes its `execution_request` row's state, UNKNOWN stays unresolved and blocks the cursor, never re-sent; a
  `planned` op without a row is dispatched once, `TryCreate` refusing a duplicate); `SuspendIfMoved` on the gateway's (connector, mode, account); `End(reason)` cancels
  each working order then closes through `CloseAsync`, the allocation and the envelope untouched. `CloseAsync` gained an optional `strategyVersionId` so the
  flatten names the run's version — without it the flatten was refused `ENVELOPE_ACCOUNT_RESERVED` on the envelope's own account, that guard working.
- **Surfaces:** `trade deployment list` / `stop --id` (a role may end its own version's paper run), `status.deployments`, report section 4, the Safety card's
  one press plus confirm; `CONTRACTS.md` "The paper deployment"; `USER-GUIDE.md`. `Every_mutating_op_dispatches_once_for_one_request_id` gained a
  `deployment-stop` arm (its own comment asked for it); nothing removed or renamed.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at the code tip `41d3759` (rebased onto `3746774`), Release
`--no-incremental`: 0 warnings, 0 errors; Unit 1181 + Fault 399 + Integration 687 = 2267 passed, 0 failed, 1 skipped (11 m 12 s); touched classes 3× →
`PaperDeploymentTests` 6, `VenueCatalogTests` + `PaperEligibleVerdictTests` 18, `ReplayedSweepSendsNothingTests` 4; names 1919 → 1925, 0 removed. RED on the base
`71dd3a9`: 57 compile errors, e.g. `PaperDeploymentTests.cs(164,23): error CS1061: 'TradingGateway' does not contain a definition for 'Deployments'`; (e) behavioural
as quoted above. Mutant (i), the op written after dispatch: `Assert.Single() Failure: The collection was empty` (`:257`; log `written before the wire: 0 — none /
orders at the wire : 1 then 0`); mutant (ii), the switch check on the mode alone: `Assert.Equal() Failure … Expected: "suspended" Actual: "active"` (`:329`); both
put back. Manager's gate at `dae8605` (the reported tip, 0 behind `main` `3746774`; `src`/`tests` identical to the code tip `41d3759`), Release: build `--no-incremental`, 19 projects → 0 warnings, 0 errors; Unit 1181/1181 (25 s), Fault 399/399 (1 m 29 s), Integration 687/688, 1 skipped (11 m 7 s, its normal length, beside two builders' work) → 0 failed. Names vs `main` (git objects): sets 1918 → 1924, 0 removed, 6 added. Scan clean; no trailers; `rev-list --count` → 0. CI at `dae8605`: recorded when complete.

**NOT done, NOT verified:** no runner emits an intent — the `entry`/`exit`/`stop`/`target` kinds are writable and never produced; the only ops this build
dispatches are an end's `flatten` and a `cancel` per working order (`U-runner` cut from this tip); the Safety card never rendered; no real model; no box, no ATAS,
no venue, no real order.

## 2026-09-20 — U-material-origin landed: a file the app writes into a role's home is measured as the app's, by path and hash, never as the agent's

The follow-up `U-language-in-home` flagged (the scanner measured every file under a role's tracked directories as `MaterialOrigin.Agent`, the shipped reference,
the three programs and the relay's deliveries included — a false origin in the one table the agent cannot edit), built by one fresh Opus builder from the 24-line
brief `docs/briefs/U-material-origin.md` (landed `d9ae716`) beside the deployment and runner legs. Merge `659eb5b`, 3 commits (2 items + the report), 15 files, +647/−34. No schema rung (`material.origin` is
TEXT with no CHECK). Not the money path; the evidence zone's measurement.

- **`MaterialOrigin.App` and the manifest:** `AppFileManifest` at `state/app-files.tsv` — outside the workspace, no verb and no pipe op writes there; `WorkspaceBuilder`,
  `ResearchLibrary` and `CouncilRelay.Deliver` record path-in-home + sha256 AFTER the bytes land; the scanner records `App` only when path AND hash match, a
  file at a manifested path with another hash is the agent's, an unmanifested path is what it was. `MaterialStore.Observe` gained a `Func<MaterialOrigin>`
  overload so bytes are read only for a row about to be written. The inbox attestation untouched.
- **Said everywhere the word appears:** `material-list` takes and prints `origin: app` with "written by TradeAgent … never counted as your work" in its note;
  `trade schema` says the same; `AGENTS.md` names the three words in one line; the owner's Inbox page prints "written by TradeAgent" instead of "the AI made
  this". The JSON field stays the machine word `app` (a deviation stated); `CONTRACTS.md`'s origin vocabulary also gained `inbox-unattested`, which the handler
  already accepted.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at the code tip `f02b917` (rebased onto `3746774`; the cherry-picked
brief commit dropped by patch-id as expected), Release `--no-incremental`: 19 projects, 0 warnings, 0 errors; Unit 1186 + Fault 393 + Integration 688 = 2267
passed, 0 failed, 1 skipped (11 m 7 s); touched classes 3× → `MaterialAppOriginTests` 5, `MaterialOverPipeTests` 6; names 1919 → 1925, 0 removed. RED on the base
(enum and manifest present, scanner unwired): `Expected: App / Actual: Agent` at `:73`, `:103`, `:195`; (c) GREEN on the base — a guard checked, not a RED. The
brief's mutant, the hash check dropped: `Expected: Agent / Actual: App` at `MaterialAppOriginTests.cs:110`; two more of the builder's own — the path half dropped
(`:147`, `:198` red) and the item-2 arm removed (`Expected: "written by TradeAgent" / Actual: "the AI made this"` `:220`; over the pipe `origin 'app' is not one
of: …` at `MaterialOverPipeTests.cs:210`); all put back. Manager's gate at `04018d0` (the reported tip `3dea0b1` rebased by the manager over `U-deployment`, `git merge-tree` having predicted no conflict and the unit's own files identical across it; landed as `659eb5b` after a docs-only rebase over `90e6185`, `src`/`tests` identical), Release: build `--no-incremental`, 19 projects → 0 warnings, 0 errors; Unit 1186/1186 (25 s), Fault 399/399 (1 m 27 s), Integration 688/689, 1 skipped (11 m 7 s, its normal length, beside the runner's work) → 0 failed. Names vs `main` (git objects): sets 1918 → 1924, 0 removed, 6 added. Scan clean; no trailers; `rev-list --count`
→ 0. CI at `659eb5b`: recorded when complete.

**NOT done, NOT verified:** the CLI help line in `TradeCli/Program.cs` (another leg's file) still omits `app` — `--origin app` reaches the gateway regardless;
`DailyReports.cs` has no material section and none was added; rows measured before this unit keep their old word (the reference and the examples correct
themselves at the next start; a delivery already in `in/` stays `Agent`); no box, no provider, no venue, no order.

## 2026-09-20 — U-runner landed: the frozen version runs forward on paper — closed bars in, the first production `PlaceIntent` from a `StrategyIntent` through every gate, protection by code, crash-safe replay

The deployment → forward intents → fills arrows (`docs/PRINCIPLES.md`: "Frozen strategies execute through the app's deterministic runner and existing gateway"),
built by one fresh Opus builder from the 40-line brief `docs/briefs/U-runner.md` (landed `636ea94`, amended `78394a2`), cut from the deployment tip, rebased onto
`a2c05be` with no conflict. Merge `44a436c`, 5 commits (4 items + the report), 10 files, +1744/−12. No schema rung. MONEY PATH in PAPER: `ForwardRuns` is the first production producer of a `PlaceIntent`.

- **Bars reach the connector and the runner:** `ForwardBarSource` binds `forward_bar` to the paper connector's `IPaperBarSource` (`Connectors.Create` builds it for
  both hosts — the adapter's empty `MemoryBarSource` is gone from production); `ForwardRuns` replays the frozen program deterministically from the deployment's
  start on every `BarClosed` and at start-up; a non-ascending bar is skipped with a note, never stepped twice; a sticky fault ENDS the deployment with the reason.
- **Protection first, by code:** at the close of the bar reaching the maximum hold a `flatten` op closes at market BEFORE `Step` is asked, so the evaluator reads
  a flat account; sizes rounded DOWN to the catalogue's verified increment, a size that rounds to nothing a recorded no-trade.
- **Dispatch through every gate:** `StrategyIntent` → `IntentDecision.From` → `PlaceIntent` (version, decision) → `deployment_op` written `planned` →
  `PlaceAsync(AgentContext.Deployment(id))` → the answer or the refusal code recorded (`DECISION_EXPIRED`, `ALLOCATION_EXCEEDED` included; a `CREATED` row and a
  `GatewayDeniedException`'s code both recorded, nothing removed); on the entry fill the stop and the target rest at the venue from the price paid, the loser
  cancelled; nothing is planned past the first bar the cursor has not reached. **Stated deviations:** a paper fill lands two bars after the signal bar, not one —
  the minute in progress had already opened when the signal was seen, and the adapter fills at the first CLOSED bar that opened after the order (a real latency,
  recorded, not hidden); a `stop`/`target` op resolves on the venue's acknowledgement rather than a terminal answer, or protection would stall the run.
- **Feedback:** fills attributed to the version and the deployment through `execution_request`; a deployment's END and each UTC day-close raise one persisted wake
  to Research with the figures (the role's own experiment, not holdout evidence); both hosts advance the runner; `CONTRACTS.md` "The runner" says what a paper
  fill is and is not.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `44a436c` (rebased onto `a2c05be`), Release `--no-incremental`: 0
warnings, 0 errors; Unit 1186 + Fault 399 + Integration 695 = 2280 passed, 0 failed, 1 skipped; `ForwardRunnerTests` 3× 7/7, `PaperDeploymentTests` 3× 6/6; names
1931 → 1939, 0 removed. RED on the base, all six: (a)(b)(d)(e)(f) `Assert.Single() Failure: The collection was empty`; (c) `Assert.Equal() Failure … Expected: 1 /
Actual: 0`. Mutant (i), the cursor advanced before the ops resolve: (e) `Assert.Single() Failure: The collection contained 2 items` — two working market sells
`TA-dp-…-29830324-0`, `TA-dp-…-29830325-0` under a long 1, the duplicated exposure the test exists to catch; mutant (ii), the maximum hold checked after `Step`:
(c) `Expected: 1 / Actual: 2`; both put back. Test (e), Astra's one pre-run insistence, is built on the max-hold flatten (the evaluator's pending guard already
covers entries) and drives the shipped `ForwardBarSource`, not the memory one. Manager's gate at `44a436c` (the reported tip, 0 behind `main` `a2c05be`; `src`/`tests` identical to the code tip `0cb8b0f`), Release: build `--no-incremental`, 19 projects → 0 warnings, 0 errors; Unit 1186/1186 (25 s), Fault 399/399 (1 m 35 s), Integration 695/696, 1 skipped (11 m 6 s, its normal length, beside the fixer's work) → 0 failed. Names vs `main` (git objects): sets 1930 → 1938, 0 removed, 8 added. Scan clean; no
trailers; `rev-list --count` → 0. CI at `44a436c`: recorded when complete.

**NOT done, NOT verified:** no forward bar from the real host has driven a deployment — every bar in every test is canned; no real model; no `trade` verb or pipe
op for the runner (none is wanted); `RunBooks.At`'s vestigial `ordinal` parameter left; no screen, no box, no ATAS, no venue, no real order. The two hosted-runner
reds of the morning (`d9ae716` windows, `ed3b224` ubuntu; the same code green at neighbouring shas) are `U-runner-reds-3`'s, in flight.

## 2026-09-20 — U-launcher-env landed: the contained child runs on the runtime the app runs on, and a launcher that fails is not "installed"

The observed run's first refusal (below, this date: onboarding step 5 never advanced on this Mac although the CLI was signed in), reproduced from a shell and
briefed the same hour; built by one fresh Opus builder from the 28-line brief `docs/briefs/U-launcher-env.md` (landed `52072f6`). Merge `b3582d7`, 3 commits
(2 items + the report). No schema rung. Not the money path: the agent runtime's child environment and its install detection.

- **The cause, RUN:** on Unix `ProcessContainment.Start` relaunches every child through the app's own framework-dependent `trade` (the containment launcher);
  the child's whitelisted environment dropped `DOTNET_ROOT`, so on a machine whose .NET is not at the default location the launcher itself printed `You must
  install .NET to run this application … DOTNET_ROOT = <not set>` and exited 131 before `codex login status` ran; the auth probe read the non-zero exit as
  NotAuthenticated, silently, every 2 s — while step 4 had PASSED because `GetVersionAsync` returned the launcher's error text as the version.
- **Item 1:** `DOTNET_ROOT`, `DOTNET_ROOT_X64`, `DOTNET_ROOT_ARM64` and `DOTNET_ROOT(x86)` join `AgentEnvironment.PassThrough` on every platform; `Apply` untouched,
  so each crosses only when the app itself holds it; the whitelist gains nothing else; `CONTRACTS.md`'s containment section says why.
- **Item 2:** a version comes only from an exit-0 run; a program that will not start is `Installed = false` with the first line it printed as
  `RuntimeDetection.Reason`, shown by the onboarding install step, the Doctor row and the `AgentRuntime` health row, and not re-downloaded. Stated deviations:
  `RuntimeDetection.Reason` added inert before the red run so the test could compile against the base; `CONTRACTS.md` also gained an `IAgentRuntime` paragraph.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at `b3582d7` (on `52072f6`), Release `--no-incremental`: 0 warnings, 0
errors; Unit 1189 + Fault 399 + Integration 695 = 2283 passed, 0 failed, 1 skipped; touched classes 3× → 6/6; names 1939 → 1942, 0 removed. RED on the base: (a)
`DOTNET_ROOT was set in TradeAgent's own environment and did not reach the child`; (b) `a program that exits 131 without running was reported as an installed
runtime`; (c) `An_exit_zero_run_is_the_version` GREEN on the base — a guard checked. Mutant (the exit code ignored in the version probe): (b) red on the same line;
restored. Measured through the real launcher: `trade --spawn-contained /bin/echo` under `env -i` → `You must install .NET to run this application.`, exit 131;
with `DOTNET_ROOT=$HOME/.dotnet` → `the vendor CLI ran`, exit 0. Manager's gate at `b3582d7` (the reported tip, 0 behind `main` `52072f6`), Release: build `--no-incremental`, 19 projects → 0 warnings, 0 errors; Unit 1189/1189 (25 s), Fault 399/399 (1 m 31 s), Integration 695/696, 1 skipped (11 m 34 s, beside the fixer's work) → 0 failed. Names vs `main` (git objects): sets 1938 → 1941, 0 removed, 3 added. Scan clean; no trailers; `rev-list
--count` → 0. CI at `b3582d7`: recorded when complete.

**NOT done, NOT verified:** "step 5 advances by itself" in the running app — the builder could not relaunch the bundle without killing the manager's parked run;
verified or refuted by the manager's own relaunch on the landed build, recorded in the observed-run section; Windows untouched (its apphost finds the shared
framework by the registry, which is why the box never showed this); no box, no money.

## 2026-09-20 → 2026-10-01 — the observed run, attempt 1: set up through the holdout, never started, lost to an eleven-day stoppage; docs only

`docs/briefs/U-observed-loop.md` (landed `c17d5c1`), run by the manager through the app's own UI on this Mac with screen control. The milestone is NOT claimed;
the brief stays in flight for attempt 2. Every line is from the manager's notes taken at the time (the scratchpad copy was lost, see below, and recovered from the
session transcript) or a database read quoted as it printed.

- **Build, home, runtime:** `tools/mac-bundle.sh` (Debug, bundle `dev.tradeagent.mac`) at `34ec647` on an emptied `$TMPDIR/tradeagent-dev` (onboarding replays),
  relaunched at `b3582d7` on the same home (pid 48188); the codex CLI 0.153.4, `codex login status` → `Logged in using ChatGPT`, exit 0.
- **The first refusal:** step 5 "Sign in to your AI account" never advanced (13:12Z) — the contained child's launcher exited 131 without `DOTNET_ROOT`, reproduced
  from a shell, briefed and landed as `U-launcher-env` (section above). **Verified in the running app at `b3582d7`: step 5 ADVANCED BY ITSELF, no press.**
- **Every press, all in the app's UI:** Get started; OpenAI Codex CLI; Practice simulator (paper is offered in Settings only); SIM-001; Create it; Start the AI;
  Finish (main screen 14:05Z, "OpenAI Codex CLI is ready"); Settings → "Use TradeAgent paper" (activity 14:06:35Z `Trading platform set to paper`); "Use this
  account" (14:11:42Z `You chose the simulated account TradeAgent paper account (PAPER-1)`, 10,000.00 USDT); "Download 12 months" (14:12Z); the holdout's From
  typed `2026-06-01`, "Hold these bars back", its armed confirm (`Confirm: bars from 2026-06-01 00:00 UTC on are evidence the research process…`).
- **Database, read only:** 14:14:47Z `dataset` 1 — BTCUSDT 1m v1 ACCEPTED, 525,600 bars, 12 of 12 months, 2025-09-01T00:00 → 2026-08-31T23:59, research, 12
  files; live bars collected by the app since 10:40Z — `forward_bar` 1,212 at 14:14Z (newest open 14:12Z), `forward_fetch` 154 (151 HTTP 200); 14:28:09Z
  `holdout_from` = `2026-06-01T00:00:00.0000000Z`, `strategy_campaign` 1 row. A second arm-and-confirm at the same date, pressed 14:28:10Z: effect NOT VERIFIED.
- **The stoppage:** at 14:28:15Z the session (and the fixer leg) ended on `Your organization has disabled Claude subscription access for Claude Code`; resumed
  2026-10-01 15:23Z. The app was not running (when it exited: NOT VERIFIED — its log did not survive); the home, the bundle and the manager's scratchpad held 0
  files, directories kept, the home's `state/` and `bin/` last changed 2026-09-24 06:53 local — the pattern of the OS's cleanup of untouched temporary files;
  which process removed them is NOT VERIFIED.
- **What did NOT happen:** the envelope press, the Research model choice, "Let the AI work on its own"; no model turn, backtest, verdict, allocation, deployment,
  order or fill. Nothing was seeded at any point.
- **Findings, not briefed:** paper is not offered at onboarding's platform step; the Settings page resets its scroll on the five-second refresh (against the "tree
  built once" convention) — reaching the holdout card took the wheel and the press in one breath; the holdout's From field shows a watermark that reads as a
  value, and the button arms only once a date is typed; one `Ipc accept_failed` warning at start (SocketException 22 in `NamedPipeServerStream.ConfigureSocket`)
  while `trade status` answered. **Changed for attempt 2:** the home lives outside `$TMPDIR`; the evidence is checkpointed into this file as the run goes.

**CI, every `main` sha since `a617486` that ran** (the run id; a push's intermediate commits run nothing). GREEN on all four jobs (ubuntu, windows, macos,
package): `d123e91` (35456163772), `413c6de` (35456865118), `0ab526f` (35456867007), `3557e1e` (35457433377), `d8000fa` (35459469327), `e89a4cb` (35459470564),
`636ea94` (35459703717), `6016fdd` (35460941975), `b715648` (35460943424), `f4d271b` (35461077856), `1616de7` (35498520780), `78394a2` (35498522879), `1031fcd`
(35498905744), `add2671` (35499906593), `8636204` (35499908559), `c17d5c1` (35500011788), `3746774` (35501982713), `dae8605` (35503269775), `c66bd41`
(35503271284), `90e6185` (35503310940), `761bd7b` (35503896856), `a2c05be` (35504025940), `44a436c` (35505686709), `34ec647` (35505688619), `52072f6`
(35513213180), `309f389` (35515295131). RED on one hosted runner each, the same code green next door, package skipped: `d9ae716` (35501396212) windows —
`SweepRequestIdTests.Every_sent_not_confirmed_leg_carries_an_unknown_record_that_will_be_reconciled` and `ValuationLossSurfacesTests.An_exit_is_reported_under_
its_own_line_and_says_no_budget_was_reached`; `ed3b224` (35501981449) and `b3582d7` (35515252973) ubuntu — `BridgeRoundTripTests.A_bridge_speaking_the_previous_
protocol_raises_no_events_into_the_application` (`Assert.Equal() Failure: Values differ`); `659eb5b` (35503895941) macos — `CouncilLoopTests.A_second_turn_for_
a_role_already_turning_is_refused_and_never_launched` (`Assert.Equal() Failure: Values differ`). All four are `U-runner-reds-3`'s; each sha stays red until it lands.

## 2026-10-01 — the observed run, attempt 2, first part: set up through the app, a real model working, and the second refusal — no `trade` call on macOS; docs only

`docs/briefs/U-observed-loop.md` (in flight), run by the manager through the app's UI (background AXPress; the wheel and keyboard under full-screen control,
approved by the owner 15:3xZ). A checkpoint, not the record: the milestone is NOT claimed. Every line is a press, a database read or a quote as it printed.

- **Build, home, runtime:** `tools/mac-bundle.sh` (Debug) at `faef463` (`git diff --quiet b3582d7 faef463 -- src tests`), home `~/Projects/ai-trading-software-for-
  mihael-worktrees/observed-run-home` (outside `$TMPDIR`, created empty 15:33:23Z), pid 80252; codex-cli 0.153.4, `codex login status` exit 0.
- **Onboarding, 15:34–15:35Z:** Get started; OpenAI Codex CLI; step 5 "Sign in … — done" 15:34:25Z BY ITSELF (U-launcher-env holding); Practice simulator;
  SIM-001; Create it; Start the AI; Finish. **Settings:** "Use TradeAgent paper" (15:35:20Z `Trading platform set to paper`); "Use this account" PAPER-1
  (15:39:56Z); "Download 12 months" → `dataset` 1 ACCEPTED 15:40:50Z: BTCUSDT 1m v1, 11 of 12 months (`2026-09` not yet published), 482,400 bars,
  2025-10-01 → 2026-08-31T23:59, 0 gaps; holdout From typed `2026-06-01`, two presses → campaign 1 opened 15:41:24Z (trials 200, verdicts 3, exploration 50,
  scoring sha 227cce19f0bc, paper policy v1 489fcd999eb5); live bars `forward_bar` 1,005 before the download (the collector's first fetch reaches back).
- **First refusal of the day, in words (Dashboard, 15:42Z):** "Cost today: 0 USD of 5 USD — but one turn can cost up to 6.4 USD, which is more than the whole
  limit, so the AI cannot start a turn at all. Raise the limit, or choose a cheaper model above." The default gpt-5.6-sol reserves 1.2 M input at the 5.00
  cache-write rate + 20 k output at 20.00 = 6.4 USD > the 5 USD default cap. **The model chosen (brief step 7):** gpt-5.6-luna, 0.324 USD reserved a turn —
  reached BY KEYBOARD (Tab ×4, Space; 15:46:17Z `The AI now runs on gpt-5.6-luna`): both model rows are one row of pills wider than the window, terra and luna
  clipped past its edge, no scroll reaches them (a UI defect). Cap 5 USD, split 50 %, wake 30 min — defaults kept.
- **A gap found by reading before any order:** `TradingGateway.cs:2707` refuses every `PlaceIntent` whose symbol is not in "Instruments it may touch", empty on
  a fresh home, and the envelope card does not say so → 15:46:59Z "Save limits" with BTCUSDT, armed `Confirm: widen "Instruments it may touch"`, confirmed
  (a ninth press, through the app's own two-press grant). **The envelope (Safety page, not the Dashboard as the brief said):** BTCUSDT, 0.05, 5000, 7 days,
  two presses → 15:47:46Z `paper_envelope` 8429f62e…, PAPER-1, max_quantity 0.05, max_notional 5000, one deployment, until 2026-10-08. UI defect: the quantity
  field is integer-formatted and SHOWS "0" while holding 0.05 (the armed label and the row prove the value).
- **Bounds stated before the start:** window 15:48Z → at most 21:48Z; AI spend ≤ the 5 USD daily cap (list-price equivalent; the codex CLI is billed to the
  owner's ChatGPT plan); paper exposure ≤ the envelope. "Let the AI work on its own", two presses → 15:48:32Z `The AI was set to work on its own`.
- **A real model at work:** attempt 1, operations, gpt-5.6-luna, `codex exec … --dangerously-bypass-approvals-and-sandbox -m gpt-5.6-luna`, ENDED exit 0 at
  15:50:16Z — 167,264 input (141,568 cached), 4,336 output, 0.0132 USD — and published a bounded brief to Research (quoted, the agent's words): "Backtest
  `strategies/examples/ma-crossover.strategy` once, with `--fees 0.001 --slippage 0.0005 --increment 0.001 --capital 10000` … Set `--to` strictly before
  `holdout_from`; do not read or infer the holdout … Do not request a verdict". Five turns by 15:54Z, 0.037 USD in all.
- **The second refusal, in words (Research's report, quoted):** `trade data list --json` → `IPC_UNAUTHENTICATED` "this connection presented a launch grant, and
  the operating system would not say which program is holding this connection, so the caller could not be identified at all" — every `trade` call of both
  roles (`engineering_log` `Ipc grant_rejected`). Cause (source): `PeerImage.ClientPath` answers on Windows only and `AppHost.cs:575/:759` apply the peer rule
  on every platform since `faf892f` (2026-09-12) — no CLI turn off Windows has reached `trade` with its grant since. Briefed `U-peer-unix` (`aea4379`); the AI
  left running meanwhile, its turns blocked and cheap.
- **Evidence limitation, stated:** the CLI runs unsandboxed as this Mac's user (survey table 4, rows 69/71/87), so the holdout bars in `state/` are readable by
  it and no verdict of this run will be claimed as protected evidence; no live authority is reachable through the app.

## 2026-10-01 — U-peer-unix landed: the agent pipe asks the kernel who is calling on macOS and Linux too, so a CLI turn's `trade` is served there and a copy still is not

The observed run's second refusal (section above: every `trade` call of both roles' codex turns refused `IPC_UNAUTHENTICATED`), built by one fresh Opus builder
from the 29-line brief `docs/briefs/U-peer-unix.md` (landed `aea4379`), rebased once. Merge `bedd218`, 4 commits (3 items + the report), 7 files, +503/−33.
No schema rung. MONEY PATH: the gateway pipe's authentication of a launch grant.

- **The cause, SOURCE:** `PeerImage.ClientPath` answered on Windows only while `AppHost.cs:575/:759` configured the peer rule on every platform since `faf892f`
  (2026-09-12), so off Windows every caller PRESENTING a grant was refused — against the intent written at `GatewayPipeServer.cs:29-38`; the 2026-09-13
  record's "the Doctor row saying the rule is unenforced elsewhere" was wrong in the other direction. No CLI turn off Windows reached `trade` with its grant.
- **(a) The kernel answers on Unix:** macOS `getsockopt(SOL_LOCAL 0, LOCAL_PEERPID 2)` on the accepted socket → `proc_pidpath`; Linux `SO_PEERCRED` (`SOL_SOCKET`
  1, 17) → `/proc/<pid>/exe`, on generic-number architectures only (powerpc, mips, alpha, sparc, parisc refused rather than misread); any failure → null →
  refused; Windows unchanged; recorded paths compared through `realpath(3)`, so a home under `$TMPDIR` (`/var` → `/private/var`) is not refused for a symlink.
  Sources cited at the code: MacOSX15.5.sdk `sys/un.h:85,89`, `sys/proc_info.h:743-744`, `libproc.h:102`, `sys/socket.h:716`; xnu `uipc_usrreq.c`,
  `libproc.c`; man7 `unix(7)`, `socket(7)`, `proc_pid_exe(5)`, `realpath(3)`; Linux `uapi/asm-generic/socket.h`.
- **(b) Four tests on the real kernel and the real `trade`** (`LaunchGrantTests`): this process is named as its own resolved path; `ToolDeployer`'s `trade`
  presenting a grant under `PeerRuleNow()` is SERVED; a byte-identical copy elsewhere is refused BY NAME; a copy inside the workspace is refused by that clause
  (the builder's addition: the only test that catches the new folder resolution being dropped). **(c)** the stale comments, the Doctor sentence and
  `CONTRACTS.md` now say the rule is answered on all three platforms and a caller the kernel will not name is refused.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at the code tip `22b424d` (on `516376c`), Release `--no-incremental`: 0
warnings, 0 errors; Unit 1189 + Fault 399 + Integration 699 = 2287 passed, 0 failed, 1 skipped (11 m 6 s); `LaunchGrantTests` 3× 10/10, `PeerImageRuleTests` 3×
8/8. RED on the base (macOS) `Failed: 4, Passed: 6`: admission `(exit 1): IPC_UNAUTHENTICATED: this connection presented a launch grant, and the operating system
would not say which program is holding this connection, so the caller could not be identified at all`; identity `Expected: "/Users/nicolasbeeckman/.dotnet/
dotnet" Actual: null`. Mutant (`if (!OperatingSystem.IsWindows()) return null;` put back): the admission test red with the same refusal; restored. Draft PR #24
(closed unmerged): run 36891262990 at `22b424d` — attempt 1 ubuntu red on `CouncilLoopTests.A_file_dropped_between_turns…` (`Actual: InboxUnattested`, not
this unit's code, first sighting), attempt 2 green on all three; the new tests passed in all six trx. Manager's gate at `bedd218` (the reported tip, 0 behind `main` `516376c`; `src`/`tests` identical to the code tip `22b424d`), Release: build `--no-incremental` → 0 warnings, 0 errors; Unit 1189/1189 (26 s), Fault 399/399 (1 m 29 s), Integration 699/700, 1 skipped (11 m 7 s, its normal length) → 0 failed. Names vs `main` (git objects): sets
1941 → 1945, 0 removed, 4 added. Scan: judged false positives excluded by name (`IpcToken`, `research.Token`, `CancellationTokenSource`, `cts.Token`, "machine
token", "the token, the turn must not be over", "Secret-scan exclusions by name"), otherwise clean; no trailers; `rev-list --count` → 0. CI at `bedd218`:
recorded when complete. Docs-only shas of the day: `faef463` (36885086416), `aea4379` (36887875562), `59f53d2` (36888058887), `516376c` (36888302768) — each
GREEN on all four jobs (ubuntu, windows, macos, package).

**NOT done, NOT verified:** the observable in the running app (the manager's relaunch, recorded with the observed run); the same-user gap is stated, not closed —
a process may connect, keep the socket in a child and exec the real `trade` (`U-contain-2`); the Doctor's no-recorded-hash sentence still reads "not checked"
though such a caller is refused; Linux seen only through CI's ubuntu runner; no box, no money.

## 2026-10-01 — U-self-wake landed: a role's own writes no longer wake it — the inbox wake fires for material that ARRIVED, not for the AI's own journal

Found in the observed run (attempt 2, first part, above: each Operations turn rewrote its own `trading/PLAN.md` and `JOURNAL.md`, the next scan raised
`inbox:<instant>` `{"added":2,"seen":20}`, and the chair woke about once a minute to find, in its own words, "Inbox and `in/` contained no new material").
Built by one fresh Opus builder from the 26-line brief `docs/briefs/U-self-wake.md` (landed `516376c`), cut from `u-peer-unix`'s tip, rebased onto `88a23a2`
with no conflict. Merge `a5166e7`, 4 commits (3 items + the report), 8 files, +298/−13. No schema rung. Not the money path: the paid-turn trigger.

- **(a) Count what arrived:** `ScanResult.AddedBy` (additions by the origin each new row was written with, counted where `MaterialStore.Observe` answers) and
  `ScanResult.Arrived` = `Inbox` + `InboxUnattested`; `AppHost.ScanMaterials` raises what `AppHost.InboxWake` returns — a wake only when `Arrived > 0`, its
  `added` counting arrivals. `Agent` (a role's own write) and `App` (the app's files; relay deliveries wake through their own `task:` events) never wake.
  The ledger is unchanged: `MaterialStore` and the hashing untouched, rows and origins asserted beside every wake.
- **(b) `InboxWakeTests`** on the real scanner, the app's `InboxWake` and the real queue: a role's own plan and journal raise no inbox wake; a file dropped in
  `inbox/` still raises one; a relay delivery wakes its recipient by its `task:` and raises no inbox wake. **(c)** `MissionEventKind.Inbox`'s summary, the
  `Reason` arm and one sentence in `CONTRACTS.md` say why "new material arrived in `../inbox`" is now true by construction.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at the code tip `f5fe843`, Release `--no-incremental`: 19 projects, 0
warnings, 0 errors; Unit 1192 + Fault 399 + Integration 699 = 2290 passed, 0 failed, 1 skipped; `InboxWakeTests` 3× 3/3; `CouncilLoopTests` 3× 11/11. RED on
the base (the old `result.Added > 0` trigger moved unchanged into the seam): `:100 Assert.Null() Failure … Actual: Tuple ("inbox:2026-10-01T17:28:04.365Z",
"{\"added\":2,\"seen\":2}")`; `:196 Assert.Single() Failure: The collection contained 2 items` (the relay's `task:` report wake plus an inbox wake); the drop
test's wake half GREEN on the base (a guard), its count half red `:148`. Mutant, the origin filter removed (`Arrived => AddedBy.Values.Sum()`): `:100` red with
the same tuple; restored. Manager's gate at `a5166e7` (the reported tip, 0 behind `main` `88a23a2`; `src`/`tests` identical to the code tip `f5fe843`; run through the builder's own gate script — the manager's had been overwritten in the shared scratchpad, same commands), Release: build `--no-incremental` → 0 warnings, 0 errors; Unit 1192/1192 (23 s), Fault 399/399 (1 m 28 s), Integration 699/700, 1 skipped (11 m 5 s, its normal length) → 0 failed. Names vs `main` (git objects): sets 1945 → 1948, 0 removed, 3 added. Scan clean, no exclusions; no trailers;
`rev-list --count` → 0. CI at `a5166e7`: recorded when complete.

**Deviations kept:** the wake payload's `added` now counts arrivals only; the engineering `scan` line gains `added_by` and `arrived`. **NOT done, NOT verified:**
an `InboxUnattested` row can also be a role's own write into `../inbox`, which still wakes the chair (no new power — a role can already ask for a `self` wake);
the observable in the running app (the manager's relaunch, recorded with the observed run); the ubuntu flake `CouncilLoopTests.A_file_dropped_between_turns…`
(`Actual: InboxUnattested`, run 36891262990 attempt 1) is untouched by this unit and not briefed; no box.

## 2026-10-01 — U-runner-reds-3 landed: four hosted-runner reds measured on all three runners, three fixed at the fixture and one argued into `Timing`; test-only

The honesty of the CI record (`docs/HOW-WE-BUILD.md` step 6), from the brief `docs/briefs/U-runner-reds-3.md` (landed `a2c05be`, amended `faef463`): a first
leg measured and fixed on draft PR #23 and was stopped on 2026-09-20 14:28Z by `Your organization has disabled Claude subscription access for Claude Code` with
nine commits and no report; a fresh finisher (2026-10-01) verified every item from the branch, corrected two comments and reported. Merge `896eaae`, 12
commits (the first leg's nine incl. its probes and their removal, the finisher's two comment fixes, the report), 6 test files. `src/` untouched.

- **1 `SweepRequestIdTests.Every_sent_not_confirmed_…`** (red `d9ae716` windows, `Assert.NotEmpty()`): the disk's spend inside one sweep measured at latency
  0, 3 rounds × 3 runs — ubuntu 4–10 ms, macos 2–6, windows 31–254 against 5000 ms of room (20×), and room costs three times itself in wall time →
  `Category=Timing` with the numbers at the test (`6ecc493`); nothing loosened.
- **2 `ValuationLossSurfacesTests.An_exit_is_reported_…`** (same run, `Assert.Contains()`): the exit pass took 26.8–32.8 / 16.5–22.4 / 712.5–1258 ms
  (ubuntu/macos/windows) of the shipped 2 s emergency budget; a 1 ms budget reproduced the red in all 9 jobs → a 20 s budget and the passes driven to the
  product's own exit record, bounded at 12 (4–5 needed) instead of a fixed count of 3 or 4 (`77b8922`).
- **3 `BridgeRoundTripTests.A_bridge_speaking_the_previous_protocol_…`** (red `ed3b224` and `b3582d7` ubuntu, `Assert.Equal()`): the event snapshot fell
  inside the refusal's own two events in 5/7/9, 9/6/7, 9/9/6 of 10 spun rounds (ubuntu/macos/windows), 0/90 polled → read after the peer's end-of-stream
  (`90b59ef`). **4 `CouncilLoopTests.A_second_turn_…`** (red `659eb5b` macos, `Assert.Equal()`): a 20 ms preemption gave 2 launches in 25–30/30 on ubuntu and
  macos, 0/30 on windows (its turn ≥ 100.9 ms) → the winner holds its lease until the loser is answered, 1 launch in 30/30 everywhere (`06c1e65`).
- **(c) Siblings:** `A_leg_that_failed_before_the_wire_…` moved into `Timing` (3000 ms room, 12×, `e41831f`); NOT moved and unmeasured, named in the report:
  `A_leg_refused_before_the_wire_…` (the SAME 5000 ms room as item 1), `The_simulators_two_latencies_…`, `A_five_order_sweep_…`, the 2 s-budget presses in
  the `Loss*SurfacesTests`, `PipeContractTests`, `An_authenticated_peer_…`, the `Barrier` races in three ledger classes. **A correction:** the brief's
  "~40 s normal" windows Unit time was wrong — the green windows jobs either side of `d9ae716` took 12–15 min, so the red one was 1.25–1.56× slow, not 29×.

**Verified by running (the finisher, quoted; then the manager's gate):** finisher's gate at the code tip `a24c083` (on `516376c`), Release `--no-incremental`: 0
warnings, 0 errors; Unit 1189/0/0 (26 s), Fault 399/0/0 (1 m 27 s), Integration 695/0/1 (11 m 4 s); 3×, 0 failed every run: Council 11, Valuation 5, Bridge
41, Sweep 44. CI on the PR: run 36896121229 at `a24c083` — ubuntu, windows, macos, package GREEN, `Timing` passed first time in all three test jobs (its windows
job 1.45–2.0× its usual time); green also at 36888851400, 35515899143, 35515732610 on the same fixture code. PR #23 closed unmerged. Manager's gate at `37e2106` (the report tip `3e066ab` rebased by the manager onto `f8a7500` — over U-peer-unix and U-self-wake — with no conflict, the branch's patch-id identical before and after, `78a14fd86190`; landed as `896eaae` after a docs-only rebase over `2fd0318`, `src`/`tests` identical), Release: build `--no-incremental` → 0 warnings, 0 errors; Unit 1192/1192 (27 s), Fault 399/399 (1 m 29 s), Integration 699/700, 1 skipped (11 m 8 s, its normal length) → 0 failed.
Names vs `main` (git objects): sets 1948 → 1948, 0 removed, 0 added. Scan clean; no trailers; `rev-list --count` → 0. CI at `896eaae`: recorded when complete. The four red shas (`d9ae716`, `ed3b224`, `659eb5b`,
`b3582d7`) stay red in the record; this landing is what was owed for them.

**NOT done, NOT verified:** the unmoved siblings above are unmeasured; the probes' "this Mac" figures were taken while the observed run was going; no box.

## 2026-10-01 — the observed run, attempt 2, second part: a real model authors, backtests and iterates in the running app; two fixes verified there; the owner's codex plan runs out; docs only

`docs/briefs/U-observed-loop.md` (in flight), same home and bounds as the first part (above). A checkpoint, not the record: the milestone is NOT claimed.

- **U-peer-unix verified in the running app:** bundle rebuilt at `88a23a2` on the same home (17:26Z; the AI had been paused, nothing cut); `Start the AI`
  and `Let the AI work on its own` (two presses) again, the pause having survived the restart as designed; from 17:27Z no `grant_rejected` in
  `engineering_log`.
- **The first arrow, observed:** 17:28:19Z the Operations Director froze the shipped `ma-crossover` program unchanged (`3b3364734ea9…`, `size fixed 1`) and ran
  `trade backtest` over dataset 1 up to 2026-05-31T23:59 (before the holdout, as its own brief said) → FAULTED, refused in words: "this window holds more
  than the 200000 bars one run may trace (about 138 days of one-minute bars) … Ask for a shorter window with --from and --to; a year is four runs of a
  quarter each." The same turn, 17:29:18Z, its own modification `7773a62ea8ef…` (`size fixed 0.05`, the envelope's ceiling; "A capital-consistent …") over
  2025-10-01 → 2025-12-31: COMPLETED, 132,480 bars, 321 trades, 74 wins, gross −2,212.24, fees 3,597.03, net −5,809.27, max drawdown 5,819.67 on 10,000.
- **Two roles, one experiment each:** Operations briefed Research to review that run; Research's report (the agent's words): "Independent review … Primary
  diagnosis: turnover/friction. Fees are 61.92% of …"; 17:37:04Z Operations' third candidate `fe6a070d2404…` "A lower-turnover moving-average crossover"
  (`fast 20 → 50`, `slow 50 → 200`) over 2025-10-01 → 2026-02-16T21:19: 410 trades, 100 wins, gross −2,647.37, fees 4,027.32, net −6,674.69. Three versions,
  three runs, all by the model, all app-computed, no verdict requested (the chair's plan: "Do not spend a verdict until a candidate has a recorded backtest
  and a clear rationale").
- **U-self-wake verified in the running app:** rebuilt at `f8a7500` at 18:00:34Z — deliberately WITHOUT pausing, as restart evidence: the running turn went
  `LOST` "the turn was launched and never reported its usage; the reservation stands" (0.324 kept as cost); after it, scans logged `"added_by":{"Agent":…},
  "arrived":0` and raised no wake; the AI waited for its next scheduled look. Before the fix landed the churn had reached 46 turns.
- **A third gap, in the restart:** the mission flag resumed (`ResumeAiOnStart`) but the runtime did not — "stopped — the AI has not been started", Agent
  runtime "unknown" — until the owner pressed "Start the AI" (18:01:5xZ); then the next turn came by itself (18:02:09Z). Briefed `U-resume-agent` (`2fd0318`).
- **A fourth gap: the owner's codex plan ran out.** Attempts `…1802091` (20 command items) and `…1808061` (0 items, 2.9 s) exited 1 with no usage, each
  charged its 0.324 reservation; codex's session log, the vendor's words: "You've hit your usage limit … try again at 10:30 PM." (`usage_limit_exceeded`);
  the card showed "OpenAI Codex CLI did not finish: Reading additional input from stdin..." and the loop planned its next look inside the limit. ~18:20Z the
  AI paused (one press, nothing running). Briefed `U-vendor-limit` (`d2c5b66`). Spend today 1.9397 USD of the 5 USD cap (four turns charged reservations).
- **Interventions so far, all through the app:** the presses listed in both parts, two pauses (15:58Z: the self-wake churn on a blocked `trade`; ~18:20Z: the
  vendor limit), two relaunches on landed builds (17:26Z, 18:00Z). Nothing seeded; no row written by hand.

## 2026-10-01 — U-resume-agent landed: a restart that resumes a working mission also starts the AI it needs, through the one start path a press takes

Found in the observed run (attempt 2, second part, above: after a relaunch the mission flag resumed but the runtime did not — "stopped — the AI has not
been started" — until the owner pressed "Start the AI"). Built by one fresh Opus builder from the 28-line brief `docs/briefs/U-resume-agent.md` (landed
`2fd0318`), rebased onto `2dab37f` with no conflict. Merge `e06e187`, 4 commits (3 items + the report), 11 files, +657/−113. No schema rung. Not the money path:
the launch path; `CONTAINMENT_REQUIRED` and every launch check unchanged.

- **(a) One start:** `AppHost.StartTheAiAsync` (`SelectedRuntimeId` → `RuntimeCatalog.Require` → `Agent.PrepareAsync` → `Agent.StartAsync`) is what the press
  (`MainWindow`, which still switches to Chat), setup's last screen and the restart all call; `ResumeOnStartAsync`, awaited last in `StartAsync`, calls it only
  when `DecideOnStart` says `Resume` and setup is complete, then `Mission.Start()`. A start that throws puts the press's words on the card ("stopped — …"), a
  `warn` activity line "The AI was not started: … (CODE)" and an engineering line; the resume swallows it, the press rethrows as before.
- **(b) `ResumeOnStartTests` (6)** drive the host's own composition (`ComposeTheAi`, extracted verbatim, over a test database) with a real `CliAgentRuntime`
  over a one-line probe: working + resuming → the runtime runs and the next due wake is taken without a press; paused → nothing; resuming off → nothing and
  the flag written back; setup unfinished → nothing; armed live and uncontained → `CONTAINMENT_REQUIRED` in the same words, no runtime; the press, setup and
  the restart through one method. **(c)** `CONTRACTS.md` and `docs/USER-GUIDE.md` ("It picks up where it left off"), one sentence each.
- **Deviations kept, judged at landing:** a runtime whose start threw is now stopped and dropped (kept, its conversation let the resumed loop launch turns
  refused at the launch, each holding a reservation) — so a refused PRESS no longer leaves a dead conversation; the press gains the card and activity lines;
  an optional `AgentPresence` seam (the product passes none); the conversation opens on its own thread; `StartAsync` awaits the start, so a RESUMING app first
  paints after the CLI's version probe (NOT measured; a follow-up candidate: start after first paint). One existing source assertion in
  `MissionControlsTests` now pins `await ResumeOnStartAsync();` instead of the old arm's text — its behavioural content moved into (b), name unchanged.

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at the code tip `bb64295`, Release `--no-incremental`: 19 projects, 0
warnings, 0 errors; Unit 1198 + Fault 399 + Integration 699 = 2296 passed, 0 failed, 1 skipped; `ResumeOnStartTests` + `MissionControlsTests` 3× 20/20. RED on
the base: `:62 the restart resumed the loop and left the AI it needs stopped`; `:160 Assert.Single() Failure: The collection did not contain any matching items
… Collection: []`; `:216 … Not found: "await _host.StartTheAiAsync();"`; the paused, resuming-off and setup-unfinished tests GREEN on the base (guards).
Mutant, the start removed from the resume: `:62` red with the same line; restored. Manager's gate at `e06e187` (the reported tip, 0 behind `main` `2dab37f`; `src`/`tests` identical to the code tip `bb64295`), Release: build `--no-incremental`, 19 projects → 0 warnings, 0 errors; Unit 1198/1198 (29 s), Fault 399/399 (1 m 28 s), Integration 699/700, 1 skipped (11 m 9 s, its normal length) → 0 failed. Names vs `main` (git objects): sets 1948 → 1954, 0
removed, 6 added. Scan: `CancellationToken` and `AiTurnAllowance(Input|Output)Tokens` excluded by name, otherwise clean; no trailers; `rev-list --count` → 0.
CI at `e06e187`: recorded when complete.

**NOT done, NOT verified:** the observable in the running app (the manager's relaunch, recorded with the observed run); the whole `StartAsync` is not run in a
test (its call to the resume is a source assertion); Windows, where the test's probe runs through `powershell -File`; the first-paint delay; no box.

## 2026-10-01 — U-vendor-limit landed: when the AI's own plan runs out, the app says so in the vendor's words, waits until the stated minute, and a turn refused before any work costs nothing

Found in the observed run (attempt 2, second part, above: the owner's codex plan ran out; the card showed stderr's first line, a turn refused at the first
request was charged its whole reservation, and the loop planned its next look inside the limit). Built by one fresh Opus builder from the 30-line brief
`docs/briefs/U-vendor-limit.md` (landed `d2c5b66`), rebased over U-resume-agent (one conflict, `MissionSentence`: main's arm kept, the hold's added). Merge
`c56540e`, 5 commits (4 items + the report), 11 files, +1151/−23. No schema rung (the refusal rides in `ai_attempt.context`). The paid-turn trigger and the
cost record; not the money path.

- **The capture, RUN once at 18:20:07Z** (codex-cli 0.153.4, the owner's plan limited): exit 1 in 4 s; stdout `thread.started`, `turn.started`, `error` "You've
  hit your usage limit. … try again at 10:30 PM.", `turn.failed` (the same sentence); stderr only "Reading additional input from stdin...". Kept verbatim in
  `VendorLimitTests` with the thread id zeroed (read by the manager before the push: no account, address or credential in it). The dated form and "try again
  later." come from `strings` of the binary — NOT VERIFIED in a stream.
- **(a) Recognised as data:** `RuntimeManifest.UsageLimit` (codex: pattern, retry regex, five formats; OpenCode none); the turn ends with
  `AgentTurnEnded.Limit` (vendor, sentence, retry instant, `BeforeAnyWork` fixed at the refusal) and the vendor's sentence as the last line the card shows.
- **(b) Waited out:** launches on the refusing runtime are held until the END of the named minute (codex drops the seconds), else now + the existing backoff;
  held roles are stepped over with their wakes kept due; the card reads "…: OpenAI Codex CLI's usage limit is reached — the AI waits until 22:31" and the
  activity log carries that plus the vendor's sentence, once. "10:30 PM" is read in the machine's zone on the local date of the event (the CLI is the app's own
  child, so both read one zone — NOT VERIFIED on Windows). **Deviation kept:** the hold is per runtime, so a role on the harness, billed elsewhere, works on.
- **(c) Charged honestly:** a turn with usage → as before; NO usage and the refusal before any item, text, tool or usage → cost 0, `context.refused` with
  `ended: VENDOR_USAGE_LIMIT`; any other turn without usage → the reservation, unchanged (the turn cut mid-work keeps 1.28 in the test, as the rule says).

**Verified by running (the builder, quoted; then the manager's gate):** builder's gate at the code tip `a39ca4f` (on `89328a0`), Release `--no-incremental`: 19
projects, 0 warnings, 0 errors; Unit 1212 + Fault 399 + Integration 699 = 2310 passed, 0 failed, 1 skipped; touched classes 3× 72/72. RED on the base
(`d2c5b66`, again `89328a0`): no launch `Assert.Single() Failure: The collection contained 2 items`; refused-before-work `Expected: 0 / Actual: 1.28`; last line
`Actual: "OpenAI Codex CLI did not finish: Reading "…`; the typed reason `error CS0246: … 'UsageLimitPlan' could not be found`. Mutant, the hold removed: the
no-launch test red with the same line; restored. Manager's gate at `6431da2` (the reported tip, 0 behind `main` `89328a0`; landed as `c56540e` after a docs-only rebase over `1b504fe`, `src`/`tests` identical), Release: build `--no-incremental`, 19 projects → 0 warnings, 0 errors; Unit 1212/1212 (29 s), Fault 399/399 (1 m 28 s), Integration 699/700, 1 skipped (11 m 9 s, its normal length) → 0 failed. Names vs `main` (git objects): sets 1954 → 1969, 0 removed, 15 added. Scan: `CancellationToken`,
`_cts.Token`, `input_tokens`, `output_tokens` excluded by name, otherwise clean; no trailers; `rev-list --count` → 0. CI at `c56540e`: recorded when complete.

**NOT done, NOT verified:** the hold lives in memory (after a restart the next turn is refused again, now at cost 0); the owner's own chat neither records nor
lifts it; the refused turn's wakes settle `failed`; no signature for OpenCode or the harness; the AppHost wiring never ran in the app (attempt 3's first step);
Windows; no box.

## 2026-10-01 — the observed run, attempt 2, end: paused at the owner's Codex plan limit; five landings from what it surfaced; the session's close; docs only

`docs/briefs/U-observed-loop.md` stays in flight: the milestone is NOT claimed. Attempt 2 ends at a legitimate boundary — the AI's allowance, the owner's
ChatGPT Codex plan, exhausted at 18:08Z ("You've hit your usage limit … try again at 10:30 PM", the vendor's words) — inside the bounds stated before the start.

- **End state, database read only (19:2xZ):** 48 turns — Operations 43 (1.5065 USD), Research 5 (0.4332 USD) — 1.9397 USD of the 5 USD cap, four of them
  charged their 0.324 reservation (two cut by a pause or a restart, two refused or cut by the vendor's limit); 3 `strategy_version` and 3 `strategy_run`, all
  the Operations Director's (FAULTED on the app's 200,000-bar bound, then −5,809.27 and −6,674.69 net); 0 `strategy_promotion` (no `trade verdict` request),
  0 allocations, 0 deployments, 0 `deployment_op`, 0 fills; `forward_bar` 1,222. The AI PAUSED since ~18:20Z; the app quit at the close; the home persists.
- **Interventions, all through the app:** the setup presses of the first part (onboarding, paper, PAPER-1, the download, the holdout, the model by keyboard,
  the instrument allowlist, the envelope, the start — two presses each where the control asks two); two pauses (15:58Z the self-wake churn on a refused
  `trade`; 18:18Z the vendor limit); two relaunches on landed builds (17:26Z paused; 18:00Z working, on purpose); "Start the AI" after each relaunch (the
  second is the gap `U-resume-agent` closes); "Let the AI work on its own" once after the first. Nothing seeded; no row written by hand; no file handed over.
- **What the run turned into code the same day:** `U-peer-unix` and `U-self-wake` — both VERIFIED in the running app (no `grant_rejected` after the relaunch;
  scans logging `"arrived":0` raise no wake); `U-resume-agent` and `U-vendor-limit` — landed, NOT verified in the running app (attempt 3's first step).
- **What did NOT happen:** a verdict request, a paper-eligible verdict, a paper allocation or deployment, a forward fill, a decision on fills; no box, no ATAS,
  no venue, no real order. **Evidence limitation, unchanged:** the CLI runs unsandboxed as this Mac's user — no verdict of this run is protected evidence.

**CI, every `main` sha of 2026-10-01 so far** (run id; GREEN on all four jobs — ubuntu, windows, macos, package — unless said): `faef463` (36885086416),
`aea4379` (36887875562), `59f53d2` (36888058887), `516376c` (36888302768), `bedd218` (36899296050), `88a23a2` (36899327748), `a5166e7` (36903561380), `f8a7500`
(36903588024), `2fd0318` (36903963568), `896eaae` (36905610297), `f32e8a3` (36905633488), `d2c5b66` (36905966992), `2dab37f` (36906065106); `e06e187` (36911732026), `89328a0` (36911737559). Pending at the close, for the next manager to record: `1b504fe`, `c56540e` (the `U-vendor-limit` merge), `dbc7bf2` and this commit.

## 2026-10-02 — the edge factory: the owner's "too stiff, no moat" direction turned into a target architecture, research and eleven ready briefs; docs only

**What happened.** The owner (2026-10-02): the software is "way too stiff" with "nothing in place that makes it a moat"; make it an evolving system that
finds temporary or durable edges by itself; then, mid-session: "do not start the actual code implementation just yet … have everything bolted down".
NO product code, test or build changed; the last gate figure stands (2310 passed at `c56540e`, above). Eight research legs, two read-only verification legs
of the briefs against `main` and one adversarial review ran; the plan was rewritten once (v2) from the review. Landed: `docs/EDGE-FACTORY.md` v2;
`docs/queue/` (READY: `U-key-host-pin`, `U-cost-model`, `U-paper-friction`, `U-runner-forward`, `U-evidence-identity`, `U-timeframe-a`, `U-timeframe-b`,
`U-venue-verify`, `U-tape-store`, `U-tape-read`, `U-decision-port`); `docs/research/2026-10-02/` (R01–R11, every external claim labelled, two calc scripts);
`docs/PRINCIPLES.md` amended; `docs/COUNCIL.md` rule 8 read precisely with three conditions; pointers in `CLAUDE.md`, `docs/HOW-WE-BUILD.md`
(`docs/queue/`), `manager-prompt.md`; the resume block rewritten (waves of two; owner questions to ask now).

**RUN on this Mac, 2026-10-02 (public endpoints, no key, nothing placed):** `fapi.binance.com` premiumIndex / openInterest / openInterestHist 5m /
fundingRate / globalLongShortAccountRatio / takerlongshortRatio → HTTP 200, 0.31–0.37 s; `data-api.binance.vision` exchangeInfo BTCUSDT → 200, tickSize
0.01, stepSize 0.00001, minNotional 5; `fapi` exchangeInfo → 528 USDT perpetuals trading (BTCUSDT tick 0.10, step 0.001, min notional 50);
`data.binance.vision/…/futures/um/daily/metrics/BTCUSDT/BTCUSDT-metrics-2026-09-30.zip` → 200; Hyperliquid info `metaAndAssetCtxs`, Deribit DVOL,
DefiLlama stablecoins, alternative.me F&G → 200; Kalshi markets → 200; `gamma-api.polymarket.com` → connect refused (the resolver answers an ISP block
address). Cointelegraph/CoinDesk RSS answer 200, but their terms forbid AI processing / robots (R03 § 5), so neither is a source.
`python3 docs/research/2026-10-02/R04-calc/calc.py` → 200 zero-skill trials on 273–365 days: expected best annualised Sharpe 3.20–2.77.
`python3 docs/research/2026-10-02/R10-calc/cascade_power.py` → P(DSR ≥ 0.95 | true Sharpe 2) 0.01–0.06 at one year and 0.52–0.82 at five; the
learning tier needs a forward Sharpe of 1.88 over a year with no prior and 0.68 with a family prior of 0.3.

**SOURCE, manager-checked at `0f47db7` (reading, NOT runtime):** `trade verdict` passes no model (`GatewayPipeServer.cs:2496`) so the judge is
`Frictionless` = fee 0, slippage 0, increment 1, capital 10,000 (`Backtest.cs:76`, `Referee.cs:218`) and a BTCUSDT size rounds to nothing
(`Backtest.cs:487-495`); BTCUSDT ships unverified (`VenueCatalog.cs:174-186`) and the observed-run home has no `venues.json`; provider keys are
memory-only (`HarnessKey.cs`); an override in `runtimes.json` replaces a built-in runtime whole, `BaseUrl` included (`RuntimeManifest.cs:716-728`), the
harness is built once per start from it (`AppHost.cs:128`) and posts the bearer key to its endpoint (`ApiConversation.cs:466-472`) — the route
`U-key-host-pin` closes; `Referee.EvaluatorVersion` is `backtest=1;metrics=1;scoring=1` and promotions already record it (`PromotionStore.cs:258`).

**NOT VERIFIED:** the six paper-line blockers of R01 § 0 and the key-redirect route at runtime (each brief's red-first test settles its own); every DOC or
SECONDARY research claim beyond its label; Jev from this Mac (no key); any venue's account-level behaviour (Kraken Futures' history and key scopes need
a probe with a real key); anything on Windows. CI for this commit: pending at the close — the next manager records it.

**CI owed by the previous close, now recorded (gh run list/view, 2026-10-02):** `1b504fe` green on all four jobs (36915963680); `c56540e`
(36917510177) and `dbc7bf2` (36917533373) RED on windows-latest only — `ResumeOnStartTests.A_restart_with_the_ai_working_starts_its_runtime_and_the_next
_due_wake_is_taken_without_a_press` failed after 25 s with "the restart resumed the loop and left the AI it needs stopped" (`ResumeOnStartTests.cs:62,79`),
Unit 1 failed / 1210 passed, ubuntu and macos green; `0f47db7`, the same `src/` and `tests/`, green on all four (36917611936). An intermittent hosted-Windows
red outside `Timing`: recorded red; a fresh fixer is owed (resume block item 6).

## 2026-10-02 — the organisation: the owner's hierarchical direction turned into `docs/ORGANISATION.md`, seven research and review legs, six ready briefs; docs only

**What happened.** Second session of the day. The owner, on hearing that the edge-factory plan left the agents' way of working for later: "agents shouldn't
choose anything there should be multiple top level managers managing teams that have a head manager that makes decisions and executing agents that do the
work smartly. (way more advanced that that) it should be a whole organisation with sub org's and so on"; then, asked: one chief plus "one invisible chief
watcher that secretly reports to me if he's not delivering or doing mistakes"; managers grow the chart; "be prepared for all, for now just plans"; the AI
proposes the risk ceiling, he confirms. Earlier answers the same session: horizon indefinite; resident in Belgium; prop firms one small channel; paper runs
"as many as we know won't melt the quota". NO product code, test or build changed; the last gate figure stands (2310 passed at `c56540e`). Legs (fresh Opus,
read-only, one report each): R12 code seams, R13 agent-organisation evidence, R14 multi-manager platforms (SEC Form ADV filings), R15 control plane, seats and
quota, R16 Belgium, R17 adversarial review of the design (42 findings, 9 HIGH), R18 verification of the briefs against `main`; the design was rewritten once
(v2) from R17 and R18. Landed: `docs/ORGANISATION.md` v2; `docs/queue/` + `U-price-rows`, `U-org-ledger`, `U-org-principals`, `U-org-rights`,
`U-org-envelopes`, `U-org-wakes` (READY, ≤ 40 lines by `wc -l`), and corrections to `U-venue-verify` (next free rung; its undo in the roll-back tests),
`U-cost-model` (the roll-back-list trap R18 C1), `U-decision-port` (ordering after the org substrate); `docs/EDGE-FACTORY.md` (header, §§ 2 and 4.8 Belgium,
§ 4.6 two-step volatility-scaled cuts, § 4.7 the organisation, § 5, § 8 tax and organisation cost, § 9 waves moved to ORGANISATION § 15, § 10 answers, § 11);
`docs/PRINCIPLES.md` ("Models own the organisation" became "The organisation"); `docs/COUNCIL.md`, `CLAUDE.md`, `manager-prompt.md` pointers; the resume
block rewritten; the research index (R12–R18) and an erratum at R12's head.

**RUN, 2026-10-02:** `git fetch` → `main` = `origin/main` = `1275aff`, clean. `gh run view 36956107010` (CI for `1275aff`): ubuntu and macos green; windows-latest
RED — `ResumeOnStartTests.A_restart_with_the_ai_working_starts_its_runtime_and_the_next_due_wake_is_taken_without_a_press` failed [25 s], Unit 1 failed /
1210 passed, Integration 609 passed / 1 skipped, Fault 394 passed; package skipped — the third red in four runs on unchanged `src/` and `tests/`, so a defect,
not a rare flake (resume item 6: a fresh fixer before W1's first gate). R17 ran `ls -d ~/.codex/auth.json ~/.codex/sessions` on this Mac → both exist: the
owner's ChatGPT login and session store every unconfined CLI seat can read.

**SOURCE, manager-read at `1275aff` (reading, NOT runtime):** `ListPrices.ReadOn = "2026-09-06"` with no GPT-6 rows, an unpriced model charged at the dearest
row (`RuntimeManifest.cs:963-971`); `CouncilRoles.MayPlaceOrders` = `role == "operations"` (`Council.cs:63`); `Versioning.DatabaseSchemaVersion = 26`
(`Versioning.cs:274`); `AiAttemptStore.Begin` reads totals and inserts in one `db.Write` (`AiAttemptStore.cs:256-281`); the daily cap `AiDailyCostCap = 5m`.

**NOT VERIFIED:** every DOC or SECONDARY claim in R13–R16 beyond its label; the plan-capacity figures (the owner's ChatGPT tier is unrecorded; R15 § 3d is
credit arithmetic); local decision-model speed on the owner's laptop; whether crypto perpetuals are open to a Belgian retail client (the FSMA texts and
Kraken's Belgian page disagree, R16); every "red at base" in the new briefs (R18 traced them by reading; nothing was run); anything on Windows.
**CI, recorded the same day (`gh run view`):** `a98f6f1` (37004472348) and `c80f422` (37004803635) — windows-latest RED on the same `ResumeOnStartTests` test
[24–25 s], ubuntu and macos green, package skipped: five reds in six runs on unchanged `src/` and `tests/`. Briefed as `docs/queue/U-fix-resume-on-start.md`
(W0, before W1's first gate), and `R-containment` briefed READY for W1; the queue holds nineteen. CI for the commit carrying this line: the next manager records it.

## 2026-10-02 — U-price-rows landed: the GPT-6 models a seat can be set to are priced from OpenAI's page as read on 2026-10-02, not at the dearest row

Built by one fresh Opus builder under build-fleet seat P from `docs/briefs/U-price-rows.md` (dispatched `8344192`), rebased onto `73cfaca` and again onto
`ca12eb9` (docs only on `main`'s side), no conflict. Merge `1baf168` (ff-only), 3 commits (items 2+3, item 4, the report), 7 files, +215/−33. No schema
rung. Spend accounting (the reservation that must stay a ceiling under the cap); no money-path file in HOW-WE-BUILD's list.

- **Rows (items 1–2):** `ListPrices.cs` gains gpt-6.1-sol 2.00 / 0.10 / 2.50 / 10.00, gpt-6-luna 0.10 / 0.01 / 0.125 / 0.50, gpt-6-sol 2.00 / 0.20 / 2.50 /
  10.00 (per M: input / cached / cache write / output; Standard tier, short context), read on 2026-10-02 from `developers.openai.com/api/docs/pricing` by
  direct fetch; `CodexModels` 9 → 12 ids as `learn.chatgpt.com/docs/models` lists them; the harness catalogue still equals opencode's (21 rows each).
- **ReadOn (item 3):** 2026-09-06 → 2026-10-02 after every existing row was re-read that day; no figure moved (all 18 quoted old = new in the builder's
  report). The figures tests pin stand; the cache-write dictionary gains 2.50 / 0.125 / 2.50.
- **Docs (item 4):** `docs/USER-GUIDE.md` (the GPT-6 models; the dearest now at least a hundred times the cheapest) and `docs/RESEARCH-REQUIRED.md` § D.
- **Item 5, the figure:** an UNIDENTIFIED turn still reserves at the dearest row — codex gpt-6-astra 1.2 M × 12.50 + 20 k × 50 = $16.00; opencode and the
  harness gpt-5.5-pro 1.2 M × 30 + 20 k × 180 = $39.60 — before and after (`Cheaper_rows_leave_the_estimate_for_an_unidentified_turn_where_it_was`).
- **Deviations, judged at landing:** items 2 and 3 share one commit (every row carries `ReadOn`; split, one commit dates rows falsely) — accepted.
  `gpt-5.6-sol`, the `codex` default: the builder read the page as printing only 4.00 / 0.40 / 5.00 / 20.00, marked promotional at least through
  2026-11-21, with no standard figure; the row keeps that figure (the same as on 2026-09-06) with a comment, not one derived from the stated reduction
  (5.00 / 0.50 / 6.25 / 30.00; the default reservation would go $6.40 → $8.10). Accepted: it is what the vendor bills today and the file takes no
  estimate. The risk is an under-charge once the promotion ends, so **a re-read of that row is owed before 2026-11-21** (§ D and the guide name it).

**Verified by running (the builder, quoted; then the manager's gate):** builder: Release `--no-incremental` 0 warnings, 0 errors; Unit 1218, Fault 399, 0
failed; 9 touched classes 3× 78/78. RED on the base `73cfaca` with the new test file alone: `A_gpt_6_luna_turn_reserves_at_its_own_rate_not_the_dearest_row`
→ `Expected: 0.160 / Actual: 16.00`, the Theory 3/3 red, both guards green (4 failed / 2 passed). Mutant, the `gpt-6-luna` row deleted: `Expected: 0.160 /
Actual: 16.00`; restored (`cmp` identical). Manager's gate at `1baf168` (the reported tip, 0 behind `main` `ca12eb9`), Release: build `--no-incremental`,
19 projects → 0 warnings, 0 errors; Unit 1218/1218 (29 s), Fault 399/399 (1 m 26 s), Integration 699/700, 1 skipped (11 m 5 s; the fleet's clean baseline
is 11 m 6 s) → 0 failed. Names vs `main` (git objects): sets 1969 → 1973, 0 removed, 4 added. Scan: `token counts` and `ApiKeyPlan` (prose about model
token usage; a type name) excluded by name, otherwise clean; no trailers; `rev-list --count` → 0 both ways.
**CI:** branch run 37019128845 at `84a2432` (`src`/`tests` identical to `1baf168`): ubuntu-latest and macos-latest success; windows-latest failure in
`ResumeOnStartTests` only — `A_restart_with_the_ai_working_…` ("left the AI it needs stopped") and `A_restart_in_the_armed_live_configuration_is_refused_…`
(its log holds only "The AI was not started: Signing in took too long…", `AI_AUTH_TIMEOUT`): the start-path timeout `U-fix-resume-on-start` is fixing,
from a diff that cannot reach the start path. Landed under the fleet's widened known-red rule (`ca12eb9`) — a judgement. CI at the merge: recorded when
complete (`fleet/ci-ledger.md`, then the next record).

**NOT done, NOT verified:** `RuntimeManifest.cs:635-637` ("mid-priced") and `:678-681` (the harness's "cheapest current model") are stale comments, left out
of `U-key-host-pin`'s hunks — owed once it lands; `docs/ORGANISATION.md:277` still says the table lacks the GPT-6 rows; no default model changed; the
running app (the Safety page's model buttons) not seen; no real turn on a GPT-6 model; whether a harness turn on one can call tools (`U-harness-responses`).

## 2026-10-02 — U-key-host-pin landed: the owner's pasted key goes only to the origin it was pasted for, never to one an agent wrote into a file

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-key-host-pin.md` (dispatched `5571328`); rebased by the manager onto `492ae79`
(seat P's U-price-rows) and `ec149ec` (docs only), src+tests patch-id identical, no conflict. Merge `3065e39` (ff-only), 5 commits (4 items + the report),
18 files, +883/−77. No schema rung (`DatabaseSchemaVersion` stays 26). Credentials (`CLAUDE.md`; `docs/EDGE-FACTORY.md` § 6.10) — not a money-path file
in HOW-WE-BUILD's list; the brief's red-first tests and two mutants were run anyway.

- **Item 1 (`36a5d96`):** `HarnessKey.Set(key, pastedFor)` holds the key with its ORIGIN (new `Security/KeyOrigin.cs`: http/https only, scheme +
  `Uri.IdnHost` + a non-default port, user-info dropped). `Held` stays the presence check (KeyHeld, health, both `StartAsync`s) and clears nothing; only
  the send path calls `ReadFor(origin)`: an exact match gets the key, anything else clears it and gets a typed `KeyRefusal(PastedFor, PointsAt)`. A turn
  reads `manifest.Endpoint` once and posts every request there.
- **Item 2 (`a25df77`):** the refusal names both origins (`Labels.HarnessKeyPastedForAnotherOrigin`) and ends the turn `key-origin-refused` before any
  request; `AgentTurnEnded.KeyWithheld` feeds a zero branch in `TurnMeter.Charge` beside the vendor-limit one: the turn costs nothing, its row reads `refused`.
- **Item 3 (`d5b0d51`):** the Safety page's box names the origin of the harness's OWN manifest; one that differs from `RuntimeCatalog.BuiltIn()`'s row
  reads "not TradeAgent's built-in address" in the caution colour, the built-in address beside it, and the key is taken only on a second press in the same
  window, bound to the address that press confirmed (refused if it moved between presses). Nothing is persisted.
- **Item 4 (`d7aa8df`):** `CONTRACTS.md` (the binding; NOT claimed: reads of this app's memory, keys handed to vendor CLIs at `OnboardingView.cs:779`,
  which those agents can read until containment; every future keyed endpoint uses this holder rule) and `USER-GUIDE.md` (the box).
- **Deviations, judged at landing:** (1) the origin comes from `KeyOrigin.Of`, not `GetLeftPart(Authority)`, which the builder measured on .NET 10.0.400
  keeping user-info (`https://<provider>@other.host` read as the provider) and Unicode look-alike hosts — accepted: stricter, and it fails closed. (2) The
  brief's "tick" is the page's existing two-press `Ui.ConfirmIf`, whose armed sentence names the address (no check box in this code-built UI; Fluent's
  paints the OS accent, not a `Theme.cs` colour) — accepted: in the window, naming the origin, never persisted, which is what the brief protects.
- **Start path:** the builder judged the diff does not reach it (the harness is built lazily; `ResumeOnStartTests` starts a CLI probe runtime and reads
  only `HarnessKey.Held`, unchanged); the manager read the `AppHost` and `MissionLoop` hunks (the latter adds one property) and agrees.

**Verified by running (the builder, quoted; then the manager's gate):** builder at `1eda5a3`: Release `--no-incremental` 0 warnings, 0 errors; Unit
1218/1218, Fault 399/399; eight touched classes 3× 66/66. RED before, on `73cfaca` against today's API: "the listener on port 51085 received 1
request(s), 1 of them carrying the key pasted for the listener on port 51084 (same host, 127.0.0.1)"; before the zero branch `Expected: 0 / Actual:
1.28`. Mutants on item 1 (pre-rebase `2b7e503`), each turning that test red with the leak line: the origin comparison removed; `ReadFor` comparing
`.Host`. Manager's gate at `b4fac3c` (carried to `3065e39`, build tree identical), Release: build `--no-incremental` 0 warnings, 0 errors; Unit
1224/1224 (29 s), Fault 399/399 (1 m 28 s), Integration 699/700, 1 skipped (11 m 4 s; clean baseline 11 m 6 s) → 0 failed. Names vs `main` (git
objects): 1973 → 1979, 0 removed, 6 added. Scan: four judged names excluded (a type name, a cancellation member, the box's mask property, a test
helper formatting the pretend key's header), otherwise clean; no trailers; `rev-list --count` 0 both ways.
**CI:** branch run 37020700007 at `1eda5a3` (this unit on `73cfaca`, before U-price-rows landed — the combination is covered by the manager's gate above
and the merge's own run): success on ubuntu-latest (12 min), macos-latest (14 min), windows-latest (28 min), package (4 min). CI at the merge: recorded
when complete (`fleet/ci-ledger.md`, then the next record).

**NOT done, NOT verified:** the Safety page was not run or looked at (its controls were pressed in tests only); no real provider; no box run. Seen, not
changed (source-read, not run): an admitted no-key turn also sends nothing yet keeps its reservation as cost (`AiAttemptStore.End:333-338`); no page
shows the Research conversation, so on screen a refusal reads only as the Safety page's "No key is held".

## 2026-10-02 — U-fix-resume-on-start landed: a version probe that outlasts its deadline is a failed probe, so a restart whose AI program is slow to first answer starts it instead of saying "Signing in took too long"

W0 of the build fleet: one fresh Opus fixer under seat P from `docs/briefs/U-fix-resume-on-start.md` (dispatched `6025a50`), rebased onto `73cfaca`, then at
landing onto `36934e9` (U-price-rows, U-key-host-pin) with the `src`+`tests` patch-id identical (`aaf29d01c06e`). Merge `c5ce2af` (ff-only), 3 commits (2 items
+ the report), 5 files, +155/−10. No schema rung. Not the money path (the AI's launch path); `CONTAINMENT_REQUIRED` and every launch check unchanged.

- **Item 1, the cause, from windows-latest** (diagnostic run 37017805967, job 110872924285, at a temporary `7743fe1` — prints and a narrowed `build.yml` —
  dropped before the proving runs; `git diff 73cfaca..0d5b157 -- .github` empty): `restarted=4853ms resume=20077ms`; card "Signing in took too long and was
  cancelled. Press Sign in again."; activity `warn` "The AI was not started: … (AI_AUTH_TIMEOUT)"; engineering "TradeAgentException: powershell.exe did not finish
  within 20s" at `CliAgentRuntime.Run` :810 ← `ProbeVersionAsync` :254 ← `DetectAsync` :232 ← `AgentSupervisor.PrepareAsync` :67 ← `StartTheAiAsync` :1111.
  PowerShell's first launch beside the full suite missed the version probe's 20 s deadline (re-measured at once: 6.5 s, then 0.2 s; the class alone passed).
  The second red seat P found on `u-price-rows` (run 37019128845, `A_restart_in_the_armed_live_configuration_…`) is the same: its one activity line is that
  sentence, thrown before the containment check.
- **Item 2, the fix, in the PRODUCT:** `ProbeVersionAsync` answers its deadline as a failed probe — not installed, found at its path, reason "<exe> did not
  answer within N seconds when asked for its version" — so the start goes on as it already did after any failed probe; a cancellation the caller asked for
  still throws. An internal `VersionDeadline` seam (product 20 s; 1 s in the two new tests). **Manager-read at landing:** `PrepareAsync` sets the runtime row
  FAILED with the reason and prepares the workspace (`AgentSupervisor.cs:77-97`); no version comparison gates anything (grep: none); onboarding re-downloads
  only when no path was found (`OnboardingView.cs:667`), so a slow program is not fetched again; the Doctor (`Doctor.cs:110`) and the install step
  (`CliAgentRuntime.cs:343`) now say the reason instead of the sign-in sentence. Test diff: 8 lines removed, all helper signatures and probe scripts made
  parameterised (`slowVersion = false` keeps the old fast probe); no assertion removed.

**Verified by running (the fixer, quoted; then the manager's gate):** fixer: Release `--no-incremental` 19 projects, 0 warnings, 0 errors; Unit 1214/1214, Fault
399/399; `ResumeOnStartTests` + `RuntimeDetectionTests` 3× 10/10. RED before the fix (this Mac): `ResumeOnStartTests.cs:106 a version answer slower than its
deadline kept the AI stopped on a restart`; `RuntimeDetectionTests.cs:83 TradeAgentException : slow-to-answer.sh did not finish within 1s`. Mutant, the resume
calling the loop only: `:62 the restart resumed the loop and left the AI it needs stopped` (also `:106`, `:198`, `:264`); restored → 7/7. Manager's gate at
`c5ce2af` (the reported tip rebased, 0 behind `main` `36934e9`), Release: build `--no-incremental`, 19 projects → 0 warnings, 0 errors; Unit 1226/1226 (31 s),
Fault 399/399 (1 m 26 s), Integration 699/700, 1 skipped (11 m 6 s, the clean baseline's length) → 0 failed. Names vs `main` (git objects): sets 1979 → 1981,
0 removed, 2 added. Scan: `cts.Token` (prose naming a cancellation token's property) excluded by name, otherwise clean; no trailers; `rev-list --count` → 0.
**CI:** three proving runs on `0d5b157`, the workflow unmodified — 37021009822, 37021014270, 37021021599: ubuntu, macos, windows and package success in each;
windows Unit 1213/1213; all 7 `ResumeOnStartTests` green each time (`A_restart_in_the_armed_live_configuration_…` 4.5 / 3.1 / 5.7 s;
`A_restart_with_the_ai_working_…` 21.2 / 15.0 / 41.6 s). `main` before it, same tree, windows: RED on the known test at `b4c17d6`, `1260a23`, `6025a50`,
`5571328` (and the five the brief lists), green at `2a766b2`, `73cfaca`, `85262d7`, `8344192`. **The fleet's known-red rule ends with this landing**: a
`ResumeOnStartTests` red on windows from here is a red. CI at the merge: recorded when complete (`fleet/ci-ledger.md`, then the next record).

**NOT done, NOT verified:** whether any proving run reached the 20 s deadline (no prints; 41.6 s in run 3); a real CLI's first launch on an owner's machine,
not measured; the turn's own PowerShell cold start still runs on the test's 60 s patience; Integration only on CI for the fixer; no box; the running app not
observed; the first paint still waits for the version probe on a resuming start (`docs/RESUME-HERE.md` item 6).

## 2026-10-02 — U-cost-model landed: the referee judges at the venue's published cost, pinned by each campaign, on the dataset's own instrument (rung 27)

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-cost-model.md` (dispatched `73cfaca`); rebased by the builder onto `36934e9`
at the manager's request (one docs conflict, `docs/RESEARCH-REQUIRED.md`: both texts kept, its C6 above main's re-dated § D), then by the manager onto
`c8d6642` (seat P's W0) and `ace127f`, `926be6e` (docs), src+tests patch-id identical. Merge `dcae0b8` (ff-only), 5 commits (4 items + report), 20 files,
+1714/−57. **Schema rung 27** (`DatabaseSchemaVersion` 26 → 27): two nullable `strategy_campaign` columns, no backfill; their `DROP COLUMN` lines joined
both roll-back lists. The evidence path (referee, campaign pin), not a money-path file in HOW-WE-BUILD's list; red-first and two mutants run anyway.

- **Item 1 (`1006b91`):** `VenueCostModel` v1 (Core/Strategy): fee per fill from a built-in code table of published standard taker rates with source
  and date (binance-spot 0.001, read 2026-10-02, R04 § 7); slippage 0.0002 labelled TradeAgent's ASSUMPTION; the step from a VERIFIED instrument row
  only; capital = new setting `JudgeCapital` (10,000; ≤ 0 reads 10,000); canonical text + sha; `Read` refuses a text that is not its sha's or not v1's.
- **Item 2 (`75d8cd8`):** the owner's existing press pins the model in the opening transaction from the dataset's venue, or refuses in words ("BTCUSDT's
  quantity step is not confirmed…") writing nothing; `Renew` copies the pin; no recorded venue ⇒ `Frictionless` labelled "no venue recorded"; a legacy
  campaign is decided inside the verdict's charge transaction (a verdict already charged in its lineage ⇒ "legacy frictionless judge", old verdicts
  untouched; else pinned from the venue, or refused BEFORE charging); `Referee.Verdict` with no caller model judges under the pin (the one production
  caller, `trade verdict`, passes none — read by the manager); the holdout card reads "Judged under".
- **Item 3 (`bb8103d`):** `InstrumentMatch.Refusal`, called by a research run before the increment, trial and run and by `Referee.RequestVerdict`
  before the charge, refuses a program whose `instrument` differs from the dataset's recorded symbol, naming both; the brief's test rewritten in place.
- **Item 4 (`864cc29`):** `CONTRACTS.md` (backtest, catalogue, campaign, verdict), `USER-GUIDE.md` (holdout card, fees, instrument), RESEARCH-REQUIRED C6.
- **Judged at landing:** two more `VenueIncrementTests` over ETHUSDT data now declare `instrument ETHUSDT` (names, assertions kept) — accepted; a venue
  with no published fee (revolut-x) is refused like an unconfirmed step; a legacy pin is written by the next verdict request, not by the migration.
  **By design:** Binance spot's built-in BTCUSDT row is `Verified = false`, so the press on a Binance BTCUSDT dataset is refused in words until
  `U-venue-verify` (W4); M0 runs after it. Start path: only rung 27's two `ALTER TABLE`s and the referee built after `LoadSettings` (builder).

**Verified by running (the builder, quoted; then the manager's gate):** builder at `617a82a`: Release `--no-incremental` 0 warnings, 0 errors; Unit 1228,
Fault 399, a local Integration 699 + 1 skipped, 0 failed; six classes 3×, 42/42 each run; after its rebase, at `e0da92e`: 0 warnings, Unit 1240, 0 failed.
RED before, on `73cfaca`: (a) "the holdout run took no trade: no-trade-on-the-holdout"; (d) expected "fees=0.001;…", actual "fees=0;slippage=0;
increment=1…"; (e) "Assert.Throws() Failure: No exception was thrown"; (f) "the press opened a campaign over a step nobody confirmed"; (b) failed to
compile (CS1061, no `JudgeCapital`); (c), (g) green as guards. Mutants, reverted: (i) `model ?? ExecutionModel.Frictionless` restored ⇒ (a) red with that
line; (ii) the instrument check short-circuited ⇒ (e) red. Manager's gate at `7ab3dce` (carried to `dcae0b8`, build tree identical), Release: build
`--no-incremental` 0 warnings, 0 errors; Unit 1242/1242 (31 s), Fault 399/399 (1 m 30 s), Integration 699/700, 1 skipped (11 m 6 s) → 0 failed. Names vs
`main` (git objects): 1981 → 1997, 0 removed, 16 added. Scan clean, nothing excluded; no trailers; `rev-list --count` 0 both ways.
**CI:** branch run 37022783765 at `617a82a` (before U-key-host-pin, U-price-rows and W0 landed; the combination is the gate's and the merge's run):
success on ubuntu, macos, windows-latest (40 min), package. U-key-host-pin on `main`: `36934e9` run 37026817205 — ubuntu, macos success; windows RED only
in `ResumeOnStartTests.A_restart_with_the_ai_working_…` ("left the AI it needs stopped"), the W0 class on a sha before W0's fix, as `2586cd0` before it.

**NOT done, NOT verified:** the Binance fee page not re-opened by the builder (read 2026-10-02 by a research leg); no box run; the holdout card not
seen on screen (`JudgeLine` unit-tested); agent-facing texts on the judge's friction unchanged; the research-run default (`U-paper-friction`), paper
friction, minimum notional and verifying BTCUSDT (`U-venue-verify`) are later units. `9a63a69`'s own CI run was still running at this record.

## 2026-10-02 — U-runner-forward landed: a paper deployment keeps acting past its first week, on quotes stamped at their bar's close and judged on one clock

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-runner-forward.md` (dispatched `a9c10c6`); the builder rebased onto
`492ae79`, `ace127f` and `4ce671f` (after U-cost-model), the manager onto `77a8f0f` (docs), src+tests patch-id identical. Merge `aa11d5a` (ff-only), 5 commits
(4 items + the report), 24 files, +960/−53. No schema rung. Money path: both order-path quote gates and the loss valuation in `TradingGateway.cs`.

- **Item 1 (`54bb296`):** the runner pages `Since` from `StartedAt` on the last open time until a page comes back short (`EveryBarSince`) and hands every
  bar to the replay and `RunBooks`; cursor, frontier and write-ahead ops unchanged. One pass over 50,000 bars (a temporary test under `suite.sh`, not
  committed): 98–281 ms, 71.0–72.5 MiB allocated a pass, ≈ 590 B held per bar — measured while this Mac was swapping (5.57 of 7.17 GB, load ≈ 4), an
  upper bound on this machine, not a property of the code; `CONTRACTS.md` states the O(age) time and memory cost.
- **Item 2 (`94d637b`):** a healthy collector looks at the next tick boundary + 2 s through ONE public helper, `TickAlignment.WaitForNextLook` (Provisioning,
  beside the collector; `U-tape-store` reuses it), `ForwardBars.LookOffset` in Core; the failure backoff and its test unchanged.
- **Item 3 (`9417359`):** the paper quote is stamped `SettledThrough + BarLength` — the close of the bar it comes from, never `+ Interval` after a gap;
  `QuoteInfo.IsStale(maxAge, now)`: both order-path gates and the loss valuation pass the gateway's clock (the one the decision gate reads), onboarding
  passes `UtcNow` explicitly.
- **Item 4 (`7ec88cc`):** the Market data row of a connector without streaming quotes allows bar + offset + 30 s = 92 s, stated on the row (keyed on the
  capability, so the streaming simulator keeps 30 s); the 30 s order gates are unchanged; `CONTRACTS.md` and `USER-GUIDE.md` (Market data) say so.
- **Judged at landing:** (1) `IPaperBarSource` gains `BarLength` because a bar's close needs its length (`PaperBook.Interval` and its meta write
  removed) — accepted. (2) The one clock turned 10 Fault + 2 Unit tests red whose simulator quotes were stamped on the machine clock under an injected
  gateway clock: `FakeConnector.QuoteClock` (default the system clock — the shipped simulator unchanged) is handed `options.Clock` by `TestEnv`; one
  `DecisionFreshnessTests` fixture widens its quote bound to 10 min, argued at the test, so the gate under test answers rather than the quote gate (a
  five-minute-old price is `QuoteClockTests`' to refuse) — accepted: no assertion moved, nothing renamed or removed.

**Verified by running (the builder, quoted; then the manager's gate):** builder at `6ec443a` (on `4ce671f`): Release `--no-incremental` 0 warnings, 0
errors; Unit 1244/1244, Fault 402/402; ten classes 3×, all green; a full local Integration on items 1–3, 702 passed, 1 skipped. RED before (base
`492ae79` + the new tests): (a) "replayed 10000" of 10,005, `Assert.Single() Failure … Collection: []`; (b) no flatten, `BarsSinceEntry = 9997`; (c)
Expected [12:01:02, …] / Actual [12:01:37.5, …]; (d) Expected 15:36:00 / Actual 15:45:00; (e) "MARKET_DATA_UNAVAILABLE — no price newer than 30s for
ES" where "ok" was expected; (g) DEGRADED. Mutants, each alone, restored identical: (i) paging removed ⇒ (a) red; (ii) alignment removed ⇒ (c) red; (iii)
the `+ Interval` stamp restored ⇒ (d) Expected 16:01:00 / Actual 16:10:00; (iv) `:2830` back on `DateTimeOffset.UtcNow` ⇒ (e) red.
Manager's gate at `aa11d5a`, Release: build `--no-incremental` 0 warnings, 0 errors; Unit 1244/1244 (32 s), Fault 402/402 (1 m 28 s), Integration 702/703, 1 skipped (11 m 9 s) → 0 failed.
Names vs `main` (git objects): 1997 → 2006, 0 removed, 9 added (8 tests, a rig helper). Scan clean, nothing excluded; no trailers; `rev-list --count` 0 both ways.
**CI:** branch run 37033753438 at `6ec443a` (this unit on `4ce671f`, the `main` it lands on but for docs): success on ubuntu-latest (12 min),
macos-latest (15 min), windows-latest (45 min), package (4 min). U-cost-model's landing push `afd1bb6`: run 37030536066 success on all three.

**NOT done, NOT verified:** the paper connector's own settle still reads one 10,000-bar page per call, so a fresh book over an older ledger catches up
over a few reads and refuses its stale price meanwhile (outside the brief; owed before M0 relies on an old ledger); the app was not run (the Settings
card and the Dashboard dot unseen); the item commits were not built one by one (the tip was); no Windows box; no order placed anywhere.

## 2026-10-02 — U-tape-store landed: the tape's clock starts — Binance USDⓈ-M context recorded as it arrives, append-only, each row classed by the app

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-tape-store.md` (dispatched `ec149ec`, on seat P's lent slot); the builder
rebased onto `4ce671f` and `8a51a16` (after U-runner-forward), the manager onto `c44425a`, `aa77717` (docs), src+tests patch-id identical. Merge `c8fc2cc` (ff-only), 5 commits
(4 items + the report), 20 files, +2761/−33. **Its own file and ladder: `state/tape.db` at version 1**; the main database is untouched
(`DatabaseSchemaVersion` stays 27). Not the money path: nothing here places an order or holds a credential (the builder's report; no gateway file).

- **Item 1 (`affe7e5`):** `TapeStore` (Core) is the only writer of `tape.db` — its own connection, WAL, `synchronous=FULL`, busy 5000; the version is read
  BEFORE anything is written (a newer file is refused with an activity line, its tables and journal untouched); an `if (have < 1)` ladder; `tape_fetch`
  (every fetch, its origin read off the URL) and `tape_obs` (payload as canonical JSON keeping the vendor's decimal strings, ≤ 64 KB); the same payload
  writes nothing, a different one is revision + 1 with its own `received_at`; no REPLACE, no UPDATE; one transaction per fetch; `AsOf` takes a required audience.
- **Item 3 (`aa7e471`, before item 2 — the class reads the rows):** `TapeSourceCatalog`: five built-in rows (premium/funding 60 s, open interest 60 s,
  OI 5-minute and the ratios 300 s, settled funding 900 s) over BTC ETH SOL BNB XRP DOGE, each with cadence, terms note and doc URL; `tape-sources.json`
  may only ADD up to 8 unkeyed rows (a built-in id, user-info, a query or a malformed series refused in words).
- **Item 2 (`d1e9e36`):** the evidence class is computed by the store from recorded fields — `O-LIVE` only for a built-in row fetched from its built-in origin
  and received within cadence + 30 s of its source time; everything else `O-ARCH` (loopback and file rows included); a revision never upgrades a class.
- **Item 4 (`8d95fbb`):** `TapeCollector` — a loop per row on U-runner-forward's `TickAlignment.WaitForNextLook` (the builder's marked stand-in replaced and
  deleted after the rebase), 10 s leash, 4 MB cap, backoff to 5 min, a 429/418 ends the look, no redirects, no backfill; started and stopped by
  `AppHost`; "Record market context" beside the live-bars toggle (default ON, one press, the only control; no verb or pipe op); `fapi.binance.com` in
  `SuiteReachesNoVendorTests`; `CONTRACTS.md` "The tape", `USER-GUIDE.md`, `docs/RESEARCH-REQUIRED.md` C5b.
- **Judged at landing:** (1) the origin rule moved to `Core/UrlOrigin.cs` because Core cannot reference Security and § 6.10 makes it ONE rule —
  `KeyOrigin.Of` now delegates; the manager compared the two bodies: byte-identical logic; HarnessKeyOrigin and HarnessRole tests 3× green — accepted.
  (2) A file row may not replace a built-in id and an unreadable file stops only its own rows (the file is agent-writable) — accepted, stricter.

**Verified by running (the builder, quoted; then the manager's gate):** builder at `9f23296`: Release `--no-incremental` 0 warnings, 0 errors; Unit
1256/1256, Fault 402/402 (both under `suite.sh`); eleven classes 3×, 73/73 each run. Mutants, each alone, restored identical: (i) `INSERT OR REPLACE` ⇒
(a) "Expected: Tuple (1, 0, 0, 1) / Actual: Tuple (1, 1, 0, 0)"; (ii) `AsOf` without `received_at <= $t` ⇒ (b) "Assert.Null() Failure … 12:10 r1
102.000"; (iii) the origin dropped from the class ⇒ (d) "Expected: "O-ARCH" / Actual: "O-LIVE"". RUN: the brief's six keyless GETs once from this Mac
at 15:28:27 UTC, HTTP 200 in 0.313–0.357 s (shapes in RESEARCH-REQUIRED C5b). Manager's gate at `a18d92a`, carried to `c8fc2cc`, build tree identical, Release: build `--no-incremental` 0 warnings, 0 errors; Unit 1256/1256 (31 s), Fault 402/402 (1 m 27 s), Integration 702/703, 1 skipped (11 m 7 s) → 0 failed.
Names vs `main` (git objects): 2006 → 2019, 0 removed, 13 added (12 tests, a fixture helper). Scan: four cancellation members excluded by name (`_stopping`, `leash`), otherwise clean; no trailers; `rev-list --count` 0 both ways.
**CI:** branch run 37053082404 at `9f23296` (this unit on `8a51a16`; `main` has since moved by docs only): success on ubuntu-latest (13 min),
macos-latest (16 min), windows-latest (40 min), package (4 min). U-runner-forward's landing push `f7f0b09`: run 37051843228 success on all three (windows 34 min).

**NOT done, NOT verified:** the app was not run (its collector would poll the real host), so the Settings card is unseen and no row has come from the
real host — `O-LIVE` is proved at store level only; Binance's API terms and rate limits not re-read; the 5-minute series' publication delay unmeasured;
the item commits not built one by one (the tip was); no box. The recording lasts only while the app runs (the keep-awake press is a later unit).

## 2026-10-02 — U-evidence-identity landed: evidence is bound to what the evaluator means, so an app release that changes no semantics withdraws nothing

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-evidence-identity.md` (dispatched `4ce671f`); the builder rebased onto
`8a51a16` (after U-runner-forward), the manager onto `a912261` (U-tape-store) and `be91995` (docs), src+tests patch-id identical. Merge `527b901` (ff-only), 5 commits (4 items + the report),
18 files, +2719/−45. No schema rung. Protects rule 9 of `docs/COUNCIL.md` (a changed assumption invalidates the evidence on it) while removing the
false positive that ended every paper run at every self-update.

- **Item 1 (`5461407`):** `Invalidation` withdraws standing when the promotion's `evaluator_version` ≠ `Referee.EvaluatorVersion` or the version row's
  `manifest` ≠ `StrategyVersions.Manifest`, and no longer compares `interpreter_build` (kept as provenance and as a hashed fact of the promotion id);
  `ForwardRuns.Frozen` ends a run whose re-parsed program id ≠ `deployment.VersionId`. `PromotionLedgerTests.A_promotion_from_another_interpreter_or
  _another_policy_does_not_stand` rewritten in place, name kept (evaluator, then manifest, then policy). The cost model's canonical text still enters the id.
- **Item 2 (`ec94b68`):** `CONTRACTS.md` defines "the interpreter build" of the V1 / PaperV1 texts (unedited, their shas still checked) as `EvaluatorVersion`
  plus the language manifest, names the protected property and shows item 1 keeps it.
- **Item 3 (`f8b237d`):** 15 golden vectors, each with an explicit execution model, over `tests/TradeAgent.UnitTests/Golden/evaluation-bars.json`; trace and
  metrics shas pinned beside `EvaluatorVersion` and the manifest; changed output with both unchanged fails, saying "bump `Referee.EvaluatorVersion` (or the
  manifest) and re-pin in the same commit". Beyond the brief: 17 real evaluator changes tried, each moving at least one pin.
- **Item 4 (`4a2fc09`):** `trade verdict` on a withdrawn standing answers "WITHDRAWN on <date> because the evaluation semantics changed from X to Y", that
  nothing may trade on it and that re-judging is not available yet (`U-rejudge`); a standing verdict is answered as it stands. CONTRACTS, USER-GUIDE.
- **Judged:** (1) the runner's end message (`ForwardRuns.cs:123-125`) also names a re-identified run, since "no longer parses" was false for it — accepted.
  (2) No withdrawal date existed (standing is computed at read), so the gateway records each semantics' first instant write-once in `kv` at construction;
  the date is the first other semantics recorded after the verdict, else "a date this installation did not record" — accepted by the orchestrator: it
  decides no standing, it dates words. (3) Tests in two places — accepted by the orchestrator.

**Verified by running (the builder, quoted; then the manager's gate):** builder at `64f2876` (on `8a51a16`): Release `--no-incremental` 0 warnings, 0
errors; Unit 1249/1249, Fault 405/405 (under `suite.sh`); five classes 3×, all green. RED before, base `Invalidation`/`Frozen` restored: "Expected:
paper_eligible Actual: invalidated"; "Expected: 0 Actual: 3" bars stepped; the base verdict reply: "Assert.NotNull() Failure: Value is null". Mutants: (i)
the `interpreter_build` compare restored ⇒ (a) red; (ii) one golden sha edited ⇒ (b) red, "1 golden vector(s) produced output other than what was pinned …";
(iii) the manifest compare removed ⇒ (d) red. Manager's gate at `f6f4855`, carried to `527b901`, build tree identical, Release: build `--no-incremental` 0 warnings, 0 errors; Unit 1261/1261 (31 s), Fault 405/405 (1 m 38 s), Integration 704/705, 1 skipped (11 m 9 s) → 0 failed.
Names vs `main` (git objects): 2019 → 2029, 0 removed, 10 added. Scan clean, nothing excluded; no trailers; `rev-list --count` 0 both ways.
**CI:** branch run 37055031758 at `64f2876` (on `8a51a16`; U-tape-store landed after it, covered by the gate and the merge's run): success on
ubuntu-latest (12 min), macos-latest (14 min), windows-latest (31 min), package (3 min). U-tape-store's landing push `a912261`: run 37059730081 success on all three (windows 38 min).

**NOT done, NOT verified:** Integration run locally only for the touched classes (in full on CI and the manager's gate); no box run; re-judging
(`U-rejudge`) not built; a few evaluator fault texts format a decimal in the machine's culture (division by zero, sizing), so such a trace can differ by
machine — a must-fix before M0 per the orchestrator (`U-invariant-traces`, which also bumps `EvaluatorVersion`); the date exists only from this build on.

## 2026-10-03 — U-fix-inbox-boundary landed: the loop asks the ledger, not a clock, whether the owner's drop folder holds an unrecorded file, so a file dropped or moved in at a turn boundary is recorded as the owner's

One fresh Opus fixer under build-fleet seat P from `docs/briefs/U-fix-inbox-boundary.md` (queued `926be6e`, amended `72b26f9` to both tests, dispatched
`77a8f0f`), rebased onto `8a51a16`, then at landing onto `923fb28` with the `src`+`tests` patch-id identical (`7aa7d245b872`). Merge `16af7c4` (ff-only),
3 commits (2 items + the report), 7 files, +322/−44. No schema rung. No money-path file; a `CLAUDE.md` protection — the inbox's provenance (the `material`
row is the scanner's measurement, written once; `Inbox` only across a window with no live agent, `docs/COUNCIL.md` rule 7).

- **Item 1, the cause — one for both tests, from ubuntu** (diagnostic run 37051859960, job 110986948031; kernel 6.17.0-1022-azure, `CONFIG_HZ=1000`, ext4):
  3000/3000 files written straight after a `DateTime.UtcNow` read carried both times EARLIER than it (worst 1296 µs, 1000 µs steps). The drop tests repeated
  in-process: MissionLoop 16/300 and council 56/300 `InboxUnattested`; in all 72 the yield before the turn (`MissionInbox.ChangedSince`: write or change time
  ≥ the last pass's start) said no, the next turn launched first, and the pass behind it recorded the file with that turn inside its window; all 608 yeses
  read `Inbox`. A file MOVED into the inbox keeps its older times, so the same miss happens on any OS (red with no seam on macOS and ubuntu).
- **Item 2, the fix, in the PRODUCT:** the yield asks the ledger — `MissionInbox.HoldsUnrecorded` → `MaterialScanner.InboxHoldsUnrecorded` (the scan's own
  walk, skip rules and budget; identity = `MaterialStore.Live`, `Observe`'s path, size and write-time tuple) says yes for an unrecorded inbox file, an
  unreadable folder, a drop past the budget or an unreadable ledger; `AppHost.MissionHost` passes its db. `AgentPresence`, `NoneSince` ("unsure is false")
  and `Observe` are untouched: only the yield's question changed, inside `PassAsync`'s exclusion.

**Verified by running (the fixer, quoted; then the manager's gate):** fixer: Release `--no-incremental` 19 projects, 0 warnings, 0 errors; Unit 1248/1248,
Fault 402/402; `MissionLoopTests` 3× 35/35, `CouncilLoopTests` 3× 12/12. RED before, deterministic, at `a45980d`: `MissionLoopTests.cs:285` (seam: the
last pass begins one tick after the file's times), `:315` (`File.Move` keeps older times; no seam), `CouncilLoopTests.cs:828` — each "Expected: Inbox /
Actual: InboxUnattested", 5/5 on this Mac, 3/3 on ubuntu (run 37053124660); the guard (nothing unrecorded → one pass a turn) green on both sides. Mutant
(`MaterialScanner.cs:347` `return true` → `continue`): 5 failed / 42 passed, the brief's two among them; restored 47/47. Stress after the fix (run
37053666824, same kernel): in-process 0/300 and 0/300 (212 of 680 drops hit the old miss, all `Inbox`); 40 fresh-process class loops 0 red. Temporary
diagnostics `3eaf410`, `932e1b4`, `4e4a54c` (prints, repeats, a narrowed `build.yml`) dropped by reset; `git diff main..717c7ef -- .github` empty.
Manager's gate at `16af7c4` (the reported tip rebased, 0 behind `main` `923fb28`), Release: build `--no-incremental`, 19 projects → 0 warnings, 0 errors;
Unit 1265/1265 (37 s), Fault 405/405 (1 m 38 s), Integration 704/705, 1 skipped (11 m 5 s, the clean baseline's length) → 0 failed. Names vs `main` (git
objects): sets 2029 → 2034, 0 removed, 5 added. Scan clean; no trailers; `rev-list --count` → 0 both ways.
**CI:** branch run 37055093743 at `717c7ef`, the workflow unmodified: ubuntu, macos, windows and package success. `main` before it, ubuntu red on the pair:
`a9c10c6` (37024865144), `926be6e` (37030431278), `4ce671f` (37030558283); first sighting 36891262990 at `22b424d`. **The fleet's judged exception for the
two dropped-file tests ends with this landing.** CI at the merge: recorded when complete (`fleet/ci-ledger.md`, then the next record).

**NOT done, NOT verified:** NTFS write times and a moved-in file on Windows (no box); the host's one-line wiring (`AppHost.MissionHost` passes its db)
compiled, not run in a test. Read-only findings of the fixer, pre-existing, NOT fixed, sent to the orchestrator: (1) a BACKWARD wall-clock step between an
agent's exit and a pass's start would let `NoneSince` say "no agent" across that agent — the false claim rule 7 exists to forbid; (2) the 30 s background
pass and the Inbox page's scan do not take the loop's `_passing`, so a role can launch beside them (the weaker word on an owner's file, not a false claim).

## 2026-10-03 — U-paper-friction landed: paper fills and undeclared research friction pay the venue cost model the referee judges with, and say so

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-paper-friction.md` (dispatched `8a51a16`); rebased by the builder onto
`c44425a` and, at landing, onto `c0ce760` (by the builder, at the manager's request) and `298bb36` (docs, by the manager) (one conflict in `Core/Trading.cs`, a doc comment beside U-tape-store's `RecordMarketContext`: both kept, the two
old paper-fee lines dropped), src+tests patch-id compared at the gate. Merge `55b1d50` (ff-only), 6 commits (4 items + the report + its rebase line). No schema change
(settings JSON; see the judged item 3). Money path: the paper connector's fills.

- **Item 1 (`7b16be5`):** Core `VenueFriction` (beside `VenueCostModel`, its fee and slippage, id `venue-cost-model-v1/<venue>`, sha of its text) and
  `FrictionInForce` (a source per number — owner, venue model, none — the fill's sentence, the owner's line); `PaperFriction` carries both; the
  connector's own default stays `None`, FRICTIONLESS as before (`PaperSettlementTests` untouched and green).
- **Item 2 (`0529769`):** new nullable `PaperFeeOverride` / `PaperSlippageOverride`; at every fill an override (0 included) is the owner's, else the venue
  model of the forward collector's venue (binance-spot); the legacy fields are read, not consulted (`git grep` at `8a51a16`: no writer); CONTRACTS says
  the settings row is agent-writable until containment. The headless gateway host applies the same rule.
- **Item 3 (`08b12d8`):** each friction number a research run leaves undeclared takes the dataset venue's model, a declared one wins (0 included).
- **Item 4 (`d2dd95c`):** the paper row reads "Fills pay 0.1% + 0.02% (assumption) — Binance spot standard taker, 2026-10-02" with two override boxes
  (one press saves, one clears); `status.paper_friction {source, fee, slippage, model{id, sha256}}`; the daily report's "paper fills pay" line;
  CONTRACTS' paper section and USER-GUIDE rewritten; the agents' texts no longer promise a frictionless default.
- **Judged at landing:** (1) no recorded venue ⇒ 0, labelled "no venue recorded" and said frictionless; a venue with no published fee is refused for an
  undeclared number (the referee's rule) — accepted. (2) The run's friction provenance is stored on a `friction:` line of the existing
  `strategy_run.increment_source` text, read back apart as `StrategyRunRow.FrictionSource` — accepted to keep the brief's no-schema rule; app-written,
  like the increment's sentence it follows; a later rung may give it a column.

**Verified by running (the builder, quoted; then the manager's gate):** builder at `2bfb74c`: Release `--no-incremental` 0 warnings, 0 errors; Unit
1252/1252, Fault 402/402; `PaperFrictionTests` 8/8 and `PaperSettlementTests` 5/5 3×; a local Integration subset 71/71. After its landing rebase:
Release `--no-incremental` 0 warnings, 0 errors, Unit 1273/1273, Fault 405/405, the two classes 3×; `git range-diff`: items 1, 3, 4 identical, item 2 differing only in `Trading.cs` context, every +/- line in src and tests as CI tested at `2bfb74c`. RED before (base `new PaperFriction(PaperFeeFraction, PaperSlippageFraction)` restored): (a) "Expected: 110.0220 Actual: 110"; (b) the same
in its one-number-overridden half. Mutant, restored identical: the venue fee branch → `0m` ⇒ (a) "Expected: 0.1100220 Actual: 0.0000000".
Manager's gate at `55b1d50`, Release: build `--no-incremental` 0 warnings, 0 errors; Unit 1273/1273 (30 s), Fault 405/405 (1 m 38 s), Integration 704/705, 1 skipped (11 m 5 s) → 0 failed.
Names vs `main` (git objects): 2034 → 2042, 0 removed, 8 added. Scan clean, nothing excluded; no trailers; `rev-list --count` 0 both ways.
**CI:** branch run 37057026434 at `2bfb74c` (on `c44425a`, before U-tape-store and U-evidence-identity landed; the combination is the gate's and the
merge's run): success on ubuntu-latest (12 min), macos-latest (15 min), windows-latest (45 min), package (4 min). U-evidence-identity's landing push
`923fb28`: still running at this record (the ledger and the next record carry it).

**NOT done, NOT verified:** the Settings row not seen on screen (the app was not run); no box; CI ran on `2bfb74c`, not on the rebased tip; an override
written into the settings row by anything but the Settings page is applied without a bound, as the old field was (agent-writable until containment).

## 2026-10-03 — U-meter-batch-1 landed: a harness turn with no key held sends nothing and is charged nothing; two default-model comments made true after the 2026-10-02 price reading

One fresh Opus builder under build-fleet seat P from `docs/briefs/U-meter-batch-1.md` (queued `aa77717`, dispatched `be91995`; the builder was stopped by the
account's usage limit before its first edit and resumed by message), rebased onto `923fb28`, then `298bb36`, then at landing onto `d99155e` with the
`src`+`tests` patch-id identical (`0d1304fa17b3`). Merge `01eb286` (ff-only), 3 commits (2 items + the report), 8 files, +201/−23. No schema rung. Spend
accounting (the day's cap counts what was billed; "unknown is never zero", `docs/COUNCIL.md` rule 3); no money-path file.

- **Item 1:** `AgentTurnEnded.KeyNotHeld`, set to `Labels.HarnessKeyNotHeld` by `ApiConversation` on the no-key return only — before a request is built,
  beside `KeyWithheld`'s (`U-key-host-pin`); `TurnMeter.Charge` charges zero on it with the other two markers and `ContextOf` writes it as `context.refused`.
  Exit code −1 and `not-started` are unchanged and are never read for money. `docs/CONTRACTS.md` (cost section) and `docs/USER-GUIDE.md` +1 sentence each.
- **Item 2:** both `RuntimeManifest.cs` default-model comments rewritten from `ListPrices.cs` as read on 2026-10-02 (gpt-6.1-sol and gpt-6-sol 2.00 / 10.00,
  gpt-6-luna 0.10 / 0.50; gpt-5.6-sol's 4.00 / 20.00 promotional through at least 2026-11-21), each saying its default predates those rows (chosen 2026-09-07
  and 2026-09-13, by `git log -S`) and is the owner's decision; no `DefaultModel`, price or reservation changed — the diff has no non-comment line.
- **Deviations, judged at landing:** the guide said a no-key harness role "takes no turns at all", but `AppHost.RuntimeForRole` keeps an owner-chosen harness
  role on the harness without a key and the loop gets a metered conversation, so it now reads "sends nothing at all" — accepted, the guide now matches the
  code. An added guard (below), beyond the brief — accepted.

**Verified by running (the builder, quoted; then the manager's gate):** builder: Release `--no-incremental` 19 projects, 0 warnings, 0 errors; Unit 1267/1267,
Fault 405/405; 3× `HarnessBudgetTests` 12/12 and the guards `VendorLimitTests` 6/6, `ApiWorkerTests` 7/7, `HarnessKeyOriginTests` 6/6, `TurnMeterTests` 9/9.
RED first on the base `923fb28` with the test alone: `A_harness_turn_with_no_key_held_sends_nothing_and_is_charged_nothing` → "Expected: 0 / Actual: 1.28" at
`row.Cost`, after its asserts that the transport saw 0 requests and the row ENDED with 1.28 reserved had passed. Mutant (`|| ended.KeyNotHeld is not null`
dropped from `Charge`): the same line red; restored (`cmp` identical). Guard `A_cancelled_harness_turn_keeps_its_reservation_though_it_ends_with_the_same_exit
_code` (pre-cancelled, exit −1, no usage → 1.28 kept); a second mutant keying the zero on `ExitCode == NotStarted` turned it red ("Expected: 1.28 / Actual:
0") with the no-key test still green — the zero is not read from an exit code. Manager's gate at `01eb286` (the reported tip rebased, 0 behind `main`
`d99155e`), Release: build `--no-incremental`, 19 projects → 0 warnings, 0 errors; Unit 1275/1275 (33 s), Fault 405/405 (1 m 40 s), Integration 704/705,
1 skipped (11 m 9 s, the clean baseline's length) → 0 failed. Names vs `main` (git objects): sets 2042 → 2044, 0 removed, 2 added. Scan: `stop.Token` (a
cancellation source's property) and "pre-cancelled token" (report prose) excluded by name, otherwise clean; no trailers; `rev-list --count` → 0 both ways.
**CI:** branch run 37080890075 at `8e4ad13` (`src`/`tests` identical to `01eb286`): ubuntu, macos, windows and package success. CI at the merge: recorded
when complete (`fleet/ci-ledger.md`, then the next record).

**NOT done, NOT verified:** the app was not run and no real turn made; a keyless harness role relaunched after a restart is read from source, not run; the
CLI's own −1 paths keep their reservation, as the brief bounds; `docs/EDGE-FACTORY.md:33`'s pointer `RuntimeManifest.cs:716-728` (`RuntimeCatalog.Read`)
now reads at `727-739` — the plan file was not edited (the orchestrator's).

## 2026-10-03 — U-org-ledger landed at schema 28: the organisation is app-minted data — a root, two divisions and the two legacy positions, written by the app, read by nothing yet

One fresh Opus builder under build-fleet seat P from `docs/briefs/U-org-ledger.md` (amended at dispatch `d0bc32f`, dispatched `5427746`); stopped twice by
the account's usage limit and resumed by message, its work in progress carried across a rebase by a local commit never pushed; rebased onto `be91995`, then
`298bb36`, then at landing onto `7ceb7c9`, `e57bee2` and `aff8bec` (docs only each time) with the `src`+`tests` patch-id identical (`6bdcef92f638`). Merge
`c62ec29` (ff-only), 5 commits (4 items + the report), 8 files, +1110/−1. **Schema rung 28** (27 is `U-cost-model`'s). No money-path file; inert.

- **Item 1, rung 28:** `org_unit`, `org_position`, `org_event` with the brief's columns; the root `org`, `div-operations` (head `operations`), `div-research`
  (head `research`) and the positions `operations` (home `agent`) and `research` (home `research`) under fixed ids with `ON CONFLICT DO NOTHING`; five
  `seeded` events, decider `app`, each `WHERE NOT EXISTS` a seed event for its id; kinds and statuses TEXT, their vocabulary validated by the store.
- **Deviation, additive, judged at landing — accepted:** SQL also holds the tree's shape — `CHECK ((parent_id IS NULL) = (kind = 'root'))`, a unique index
  allowing one root, foreign keys unit→parent, position→unit, event→unit/position — and none on `head_position_id`, because a division and its head name each
  other and rungs run in autocommit; `The_chart_s_shape_is_sql_s_and_its_vocabulary_is_the_store_s` tests it.
- **Item 2:** `OrgStore` reads `Units`, `Positions`, `Position`, `UnitOf`, `Ancestors`, `Subtree` and static `IsAppPrincipal` (`referee`, `allocator`;
  `perception` reserved for `U-decision-port`); no public writer; a loop or a missing parent throws `STATE_DATABASE_CORRUPT` rather than answer in part;
  `AppPrincipals.Allocator` re-spells `TradingGateway.PaperAllocatorRole` (Core cannot reference the gateway) and test (b) holds them equal.
- **Items 3–4:** both roll-back lists (`VenueCatalogTests`, `PaperEligibleVerdictTests`) drop the three org tables before restamping; `docs/CONTRACTS.md` "The
  organisation ledger" (app measurement; titles and charters will be publications; app-only writers; NOT claimed: protection from a CLI agent that writes
  `state/` directly — advisory until containment). **The next rung (`U-venue-verify`) must extend `OrgLedgerTests.StampBack` as well as those two lists.**

**Verified by running (the builder, quoted; then the manager's gate):** builder: Release `--no-incremental` 19 assemblies, 0 warnings, 0 errors; Unit
1272/1272, Fault 405/405; 3× `OrgLedgerTests` 7/7, `VenueCatalogTests` 8/8, `PaperEligibleVerdictTests` 10/10. RED first on the base `5427746` with the
tests alone: (a) and (c) "SQLite Error 1: 'no such table: org_unit'."; (e) "the organisation ledger needs schema 28 or later; this build says 27"; (b) and
(d) came with item 2, (d) a guard (no pipe op writes an org table; org-table writes in `src/` only in `Database.cs`; `OrgStore`'s public surface pinned).
Mutant, both seed-event `WHERE NOT EXISTS` removed: only (c) red — "Expected: [div-operations|1, div-research|1, org|1] / Actual: [div-operations|2,
div-research|2, org|2]"; restored, green. Manager's gate at `cf8789f` (the reported tip rebased onto `7ceb7c9`), Release: build `--no-incremental`, 19
projects → 0 warnings, 0 errors; Unit 1282/1282 (31 s), Fault 405/405 (1 m 38 s), Integration 704/705, 1 skipped (11 m 10 s, the clean baseline's length)
→ 0 failed; CARRIED to `c62ec29` by `land.sh`'s rule (only docs moved on `main`: `e57bee2`, `aff8bec`; the build tree identical). Names vs `main` (git
objects): sets 2044 → 2051, 0 removed, 7 added. Scan clean; no trailers; `rev-list --count` → 0 both ways.
**CI:** branch run 37081786583 at `316aab4` (`src`/`tests` identical to `c62ec29`): ubuntu, macos, windows and package success. CI at the merge: recorded
when complete (`fleet/ci-ledger.md`, then the next record).

**NOT done, NOT verified:** the app was not run; Integration ran only on CI for the builder; nothing reads the ledger yet; the box was not used.

## 2026-10-03 — U-paper-settle landed: one settle catches a paper book up to the newest forward bar however far behind it starts, its watermark and close one write

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-paper-settle.md` (briefed from a read-only survey leg, item 2 added by the
manager from its finding; dispatched `f4dab1d`); the builder rebased onto `be91995`, `923284e` (after U-paper-friction) and `d99155e`, the manager onto
`3301781` (U-meter-batch-1 and U-org-ledger in), src+tests patch-id compared at the gate. Merge `65083cc4` (ff-only), 4 commits (3 items + the report). No schema change. Money path: the paper
connector's settle, which prices every paper order's quote; owed before M0 (the orchestrator, 2026-10-02).

- **Item 1 (`dd47090f`):** `SettleAsync` reads again from the book's own watermark until a read brings nothing past it — `EveryBarSince`'s pattern, keyed on the
  watermark because a connector cannot know a source's page size — checking for cancellation before every read; nothing filtered, caught or held across
  calls; a later page that throws propagates with every earlier bar settled; `IPaperBarSource`'s and `SettleAsync`'s docs say so.
- **Item 2 (`759882fe`):** `PaperBook.MarkSettled` reads the watermark and writes it and the close in ONE transaction: before, two commits, so a kill between them
  quoted the previous bar's close stamped at the new bar's close — a stale price that read fresh.
- **Item 3 (`f073215d`):** measured (a temporary test under `suite.sh`, not committed): one fresh-book settle over 50,000 bars through the real
  `ForwardBarSource` takes 2.18–2.46 s over six runs, ≈ 376 MiB allocated, live heap ≤ 6.3 MiB above start (2.5–3.3 s with one working order; swap 6.2 of
  7 GiB in use, no swap-out); `CONTRACTS.md` states O(backlog) time, O(page) memory, the figure and that the 5 s `WorstCaseOperationPath` bounds neither
  this nor one page; `USER-GUIDE.md`: the first look after switching to paper can take a moment.
- **Judged at landing:** test (f) faults the second write with a SQLite trigger on the book's own file instead of a product seam — accepted, it adds no
  test-only code to the product; two tests beyond the brief's list (a later page that throws; a cancellation between pages) — accepted.

**Verified by running (the builder, quoted; then the manager's gate):** builder at `1861393` (on `d99155e`): Release `--no-incremental` 0 warnings, 0
errors; Unit 1273/1273, Fault 411/411 (under `suite.sh`); `PaperSettlementTests` 10/10, `PaperBookTests` 4/4, `PaperGatewayTests` 2/2, 3× each; an
Integration subset 21/21. RED before (base `be91995`, tests only): (a) "GatewayDeniedException : no price newer than 30s for BTCUSDT"; (b) "Assert.Single()
Failure: The collection was empty"; (c) "1 read(s), asked from 00:00; quote 103 stamped 10:04"; (f) "watermark 10:01, close 100"; (d), (e) green as
guards. Mutants, each alone, reverted: (i) the loop removed ⇒ (a) red; (ii) the stop on a short page ⇒ (c) red; (iii) the two writes as two commits ⇒ (f)
"Expected: 2026-09-19T10:00 … Actual: 2026-09-19T10:01". Manager's gate at `65083cc`, Release: build `--no-incremental` 0 warnings, 0 errors; Unit 1282/1282 (34 s), Fault 411/411 (1 m 40 s), Integration 705/706, 1 skipped (11 m 17 s) → 0 failed.
Names vs `main` (git objects): 2051 → 2059, 0 removed, 8 added (7 tests, a paging double). Scan: one cancellation member excluded by name (`cancel`), otherwise clean; no trailers; `rev-list --count` 0 both ways.
**CI:** branch run 37082332809 at `1861393` (on `d99155e`; U-meter-batch-1 and U-org-ledger landed after it, covered by the gate and the merge's run):
success on ubuntu-latest (12 min), macos-latest (15 min), windows-latest (38 min), package (4 min). Earlier landings' pushes: U-evidence-identity `923fb28` run 37079001165 and U-paper-friction `923284e` run 37081949389, both success on all three.

**NOT done, NOT verified:** the settle not measured on Windows (CI runs the tests, prints no figure); per-bar cost with many orders not measured (one
working order); the 5 s `WorstCaseOperationPath` stated, not changed; the app not run.

## 2026-10-03 — U-timeframe-a landed: a program declares the bar it is evaluated on, and backtests and the referee judge it on hours and days, not fee-eating minutes

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-timeframe-a.md` (dispatched `298bb36`; pointer corrections in its prompt);
built on `7ceb7c9`, rebased by the manager onto `3b03041` (U-org-ledger and U-paper-settle in), src+tests patch-id identical. Merge `c0090bef` (ff-only), 7 commits (6 code + the report), 23
files, +2332/−120. No schema rung, no manifest or `EvaluatorVersion` change: every v1 program's text, id and golden vector is unchanged.

- **Item 1 (`dbb147f5`):** `bars DURATION` (1m 5m 15m 30m 1h 4h 1d) is a declaration only as a line's first word and never reserved (a stored `const bars`
  keeps parsing); the canonical form writes the `bars` line only when declared and not 1m — `Header` and manifest unmoved, so `bars` rides with no bump.
- **Item 2 (`4564b411`):** one decimal `BarResampler` on a `BarGrid` (UTC; `1d` at the program's zone midnight, 23/25 h on DST days); a partial bar carries
  its minute count on `KlineBar.Minutes`; an empty window is a gap, never filled; `MissingMinutes` still counts minutes.
- **Item 3 (`4463c5ac`):** `Backtest.Run` is a two-clock loop — each minute fills at its open and fires stops and targets on its range exactly as before; at a
  declared close the max hold, then `Step`; one `Bar` trace line per evaluated bar; the cap counts evaluated bars, so a year of hourly bars is one run.
- **Item 4 (`557b328c`, with `f6ee6f9a` and `132e519c`):** limits count declared bars (500 hourly ≈ 20.8 days); `ForwardRuns` ends a deployment whose program declares
  `bars` ≠ `1m` before its first bar, in the brief's words; `STRATEGY-LANGUAGE.md`, the agents' language bullet, `CONTRACTS.md`; the two guards moved
  into a class written against the base's own API; `BacktestEvent` says what its ordinal and instant name on a declared-bar run.
- **Judged at landing (accepted, as the orchestrator also judged):** (1) `Run` reads the declared bar off the program (`barInterval` removed) and
  `EvaluationState.Start` refuses any other bar — a program cannot be judged on a bar it did not declare. (2) A run ending inside a declared bar closes
  it as a partial bar (stated in the docs). (3) ADDITION on the money path: `StartPaperDeploymentsDue` starts no replacement for a version the runner
  refuses — without it each sweep would start one, the runner end it, and Research take a paid wake per sweep (the churn family of 2026-10-01).
  `U-timeframe-b` removes that guard together with the refusal, keeping a sweep that never churns (its brief is amended at dispatch).

**Verified by running (the builder, quoted; then the manager's gate):** builder at `389950b` (on `7ceb7c9`): Release `--no-incremental` 0 warnings, 0
errors; Unit 1325/1325, Fault 408/408; five classes 3×, all green. RED before, on base `d99155e`: (a) (c) (d), cap, max-hold, lookback, partial, Over,
(e), (g) and the no-replacement test fail at "`bars` is not a declaration this language has"; (f), the 1m cap and 1m replacement green. Mutants, each
reverted: (i) resampler bypassed ⇒ (a) "Expected: COMPLETED Actual: FAULTED"; (ii) cap counting minutes ⇒ (d) FAULTED at minute 200,001; (iii) `bars`
reserved ⇒ (f) "`bars` already means something in this language"; the sweep guard removed ⇒ "Expected: [0, 0, 0] Actual: [1, 1, 1]".
Manager's gate at `c0090be`, Release: build `--no-incremental` 0 warnings, 0 errors; Unit 1332/1332 (35 s), Fault 414/414 (1 m 40 s), Integration 705/706, 1 skipped (11 m 19 s) → 0 failed.
Names vs `main` (git objects): 2059 → 2090, 0 removed, 31 added. Scan clean, nothing excluded; no trailers; `rev-list --count` 0 both ways.
**CI:** branch run 37085481666 at `389950b` (on `7ceb7c9`; U-org-ledger and U-paper-settle landed after it, covered by the gate and the merge's run):
success on ubuntu-latest (12 min), macos-latest (15 min), windows-latest (36 min), package (4 min). U-paper-settle's landing push `3b03041`: still running at this record (the ledger and the next record carry it).

**NOT done, NOT verified:** the paper runner stepping declared bars (`U-timeframe-b`); Integration only on CI and the gate; no box; the pipe's backtest
reply does not name the bar; a partial bar's shortfall counts window minutes outside `from` / the data's end (stated); on declared bars a minute out of
order is refused before its fills, unlike 1m.

## 2026-10-03 — U-invariant-traces landed: a trace, its hashes and its fault words are the same bytes under any culture; InvariantGlobalization pinned

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-invariant-traces.md` (briefed from a read-only survey leg, amended on the
orchestrator's decisions: the pin test and `KlineNormaliser`; dispatched `12f30542`, after U-timeframe-a); built on `35606c56`, rebased by the manager
onto `a68b14b4`, `54013b6d` (docs), src+tests patch-id identical. Merge `dec23ca8` (ff-only), 4 commits (3 items + the report). No schema change. **`Referee.EvaluatorVersion` →
`backtest=2;metrics=1;scoring=1`**, every golden vector re-pinned in the same commit: a standing verdict recorded under `backtest=1` is withdrawn by
U-evidence-identity's rule (none exists on any install the fleet controls — the orchestrator's statement, not a measurement). Not the money path.

- **Item 1 (`4b1deace`):** the `TraceText` interpolated-string handler — a decimal through `StrategyParser.Number`, everything else invariant — is the ONLY
  parameter type of `EvaluationFault`, `EvaluationOutcome.Faulted`, the state's `Fault` and `BacktestEvent.Fault`/`.NoTrade`, so a culture-formatted string
  does not compile in (CS1503; a test fails if a string overload appears); `Backtest.Run`'s halts through one `Halt`; two faulting golden vectors added
  (shas re-derived independently), the fifteen old pins byte-identical apart from the bump.
- **Item 2 (`63ba5ece`):** the audit re-run on this `main` as a typed Roslyn scan (23 `Strategy/` files, 369 interpolation holes): every integer and date reaching
  a version id, the manifest, a canonical text, the run, promotion and publication ids, the metrics' words and the venue friction's id names
  `InvariantCulture`; `KlineNormaliser` writes bar times invariantly; no id pin moved.
- **Item 3 (`92248328`):** test (e) pins `InvariantGlobalization` (props, project, runtime and CI files, the shipped runtimeconfigs) and names the culture tests
  that must pass before it may be turned off; `CONTRACTS.md` states the rule with its one exception and seventeen vectors.
- **Judged at landing:** no-trade words go through `TraceText` too (the same hashed field) — accepted; positive integers in REFUSAL words stay ambient (in
  no trace, hash or id; spelled alike in all 1,063 ICU cultures of this Mac, measured; stated in CONTRACTS) — accepted; U-timeframe-a's new fault texts
  carried ambient integers and now go through `TraceText` — accepted.

**Verified by running (the builder, quoted; then the manager's gate):** builder at `bcba2e2`: Release `--no-incremental` 0 warnings, 0 errors; Unit
1338/1338, Fault 414/414 (under `suite.sh`); three classes 3×. RED before, on a `git archive` of `12f3054`: (a) "…divided 1026.70 by zero…' became
'…divided 1026,70 by zero…'"; (b) "…'a rule divided 100.50 by zero…' where the trace's spelling is '…100.5…'"; (c) "under nl-BE's number format the recorded
fault reads '…144,0…'"; (d) "the dataset file is … 2026-01-05T00.00.00Z"; (e) with the props false: "…sets InvariantGlobalization to [false]…". Mutants:
(i) ⇒ (a), (b), (c) and the pin check red; (ii) `Number` without its culture ⇒ Unit "Failed: 4" — (a)–(d), nothing else. Also run: a temporary build with
`InvariantGlobalization=false` passed (a)–(d) against the REAL nl-BE and fr-BE (macOS ICU); (e) failed, as designed.
Manager's gate at `b15509a`, carried to `dec23ca8`, build tree identical, Release: build `--no-incremental` 0 warnings, 0 errors; Unit 1338/1338 (6 m 22 s — the Unit suite ran ~11× slower on this Mac after it slept: re-run alone 6 m 24 s, U-venue-verify's build 7 m 28 s under the same conditions, this unit's two classes 401 ms and 151 ms alone — environmental, all green), Fault 414/414 (1 m 43 s), Integration 705/706, 1 skipped (11 m 11 s) → 0 failed.
Names vs `main` (git objects): 2090 → 2096, 0 removed, 6 added. Scan clean, nothing excluded; no trailers; `rev-list --count` 0 both ways.
**CI:** branch run 37102017452 at `bcba2e2` (on `35606c56`; `main` has moved by docs only since): success on ubuntu-latest (12 min), macos-latest (15
min), windows-latest (43 min), package (4 min). Earlier landings: `3b03041` (U-paper-settle) windows RED in `LossHoldSurfacesTests.A_release_is_told_with_
the_owners_note_quoted` (first sighting; a gateway test over the Fake connector; seat P's `U-fix-loss-reopen`); `88c5a2f` (U-timeframe-a) success ×3.

**NOT done, NOT verified:** Integration on CI and the gate only; no box; the real-culture run is macOS only, not Windows; refusal integers left ambient.

## 2026-10-04 — U-venue-verify landed at schema 29: the app checks an instrument against the venue's own published definition and serves it verified, no file edited

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-venue-verify.md` (dispatched `e57bee24`; resumed twice after usage limits);
built on `35606c56`, rebased by the manager onto `b757bbd3` (U-invariant-traces and docs in), `e32c683c` (docs), src+tests patch-id identical. Merge `35e95db9` (ff-only), 6 commits (4 items, three more tests, the
report), 35 files, +2272/−121. **Schema rung 29** (`DatabaseSchemaVersion` 28 → 29, after seat P's `U-org-ledger` at 28): `instrument_check`; its
`DROP TABLE` joined both roll-back lists and `OrgLedgerTests.StampBack`. The cost model's step, the paper connector's offer and the runner's sizing.

- **Item 1 (`6ee4ed5a`):** rung 29 `instrument_check` (the brief's 15 columns), one append-only row per attempt; `InstrumentCheckStore` its only writer
  (origin read off the URL, outcome validated).
- **Item 2 (`44cc9c94`):** `InstrumentVerifier` (Provisioning) reads the venue's definition from the BUILT-IN shape (`VenueCatalog.DefinitionShape`); a
  `venues.json` `definition_url` is honoured on the same origin only, any other origin recorded `refused-origin` with nothing sent (`UrlOrigin`); 10 s
  leash, 4 MB bound, no redirects; a failure is a row and an activity line; it runs at start, on a pair change, on "Check now", and every 6 h.
- **Item 3 (`d32c49ec`):** ONE served read (`VenueStore.Instruments/Instrument/Catalogue`): the catalogue overlaid by the latest successful check ≤ 7 days old
  (a disagreeing catalogue number kept beside it), on each reader's own clock — Backtests, ForwardRuns, Referee, `CampaignStore.Open`, venue-list, status,
  and the paper connector through `ConnectorChoice.PaperInstruments`, read at every use, so a check that succeeds or lapses reaches it without a restart.
- **Item 4 (`aa130122`, tests `cbeb34e1`):** the Market data card's line and one-press "Check now"; `status.instrument_check`; venue-list's check fields; the six
  "edit venues.json" texts (and the agent schema, the venue-list note, a CLI comment) name the in-app check; CONTRACTS (claimed / NOT claimed / advisory
  until containment), USER-GUIDE, RESEARCH-REQUIRED C5c.
- **Judged at landing (accepted, as the orchestrator also judged):** a checked pair the catalogue holds no row for is served from its check on a venue it
  holds (else a verified check nothing serves); the 6-hourly re-check (a running app never lapses at 7 days); `VenueCostModel.For` reads the served row,
  so a verified BTCUSDT lifts U-cost-model's refusal of the campaign press; three assertions moved from `Contains("venues.json")` to `Contains("Check now")`
  plus `DoesNotContain("venues.json")` — none removed or renamed.

**Verified by running (the builder, quoted; then the manager's gate):** builder at `a39e658` (on `35606c56`): Release `--no-incremental` 0 warnings, 0
errors; Unit 1345/1345, Fault 414/414; eight classes 3×, all green. RED before: (a) with the served read answering the catalogue's own rows
"InstrumentCheckTests.cs:143 Assert.True() Failure"; (e) with the connector reading its catalogue once at construction "Assert.Single() Failure: The
collection was empty". Mutants, reverted: (i) 7-day freshness removed ⇒ (c) "a check older than seven days still verified the instrument"; (ii) the origin
check removed ⇒ (d) "Expected: \"refused-origin\" Actual: \"verified\""; (iii) is (e)'s red line. One real read: `exchangeInfo?symbol=BTCUSDT` on
the market-data API at 04:48:54Z → 200, tick 0.01, step 0.00001, minQty 0.00001, minNotional 5. Manager's gate at `3c4f8ca`, carried to `35e95db9`, build tree identical, Release: build `--no-incremental` 0 warnings, 0 errors; Unit 1351/1351 (7 m 28 s — this Mac's slowed Unit suite, measured environmental, see the U-invariant-traces record), Fault 414/414 (1 m 46 s), Integration 706/707, 1 skipped (11 m 11 s) → 0 failed.
Names vs `main` (git objects): 2096 → 2110, 0 removed, 14 added. Scan: the request leash's cancellation member excluded by name (`leash`), otherwise clean; no trailers; `rev-list --count` 0 both ways.
**CI:** branch run 37100662391 at `a39e658` (on `35606c56`; `main` has since gained docs and U-invariant-traces, covered by the gate and the merge's run):
success on ubuntu-latest (12 min), macos-latest (16 min), windows-latest (41 min), package (4 min). U-invariant-traces' landing push `6a24e12`: still running at this record (the ledger and the next record carry it).

**NOT done, NOT verified:** the card not seen on screen (the app's start would call the real host); the AppHost triggers (start, 6-hourly, pair change,
Check now) have no automated test; minimum notional recorded, not applied; a `"verified": true` written into `venues.json` is still honoured (advisory until
containment, said in CONTRACTS); Windows only through CI.

## 2026-10-04 — U-timeframe-b landed: the paper runner steps a program's rules on its declared bars while protection still acts within the minute

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-timeframe-b.md` (amended at dispatch on the orchestrator's order,
`5f4bd44a`; dispatched `3dff4294`); built on `35606c56`, rebased by the manager onto `5a040a56` (U-invariant-traces, U-venue-verify and docs in), src+tests patch-id identical. Merge `7fc9e792` (ff-only), 4
commits (3 items + the report). No schema change. Money path: the runner places orders through the gateway (`TradingGateway`'s dispatch region untouched).

- **Item 1 (`36d3cfe8`):** every minute the runner still settles its ops, cancels the losing protective order, places protection on an entry fill and checks
  the max hold (counted in declared bars, `BarGrid.Between`); only `Step` waits for a declared close, on the resampled bar, its ops keyed on the minute
  that closed it. U-timeframe-a's refusal AND its sweep guard went in this same commit (item 1 cannot be observed behind the refusal; the amendment forbade
  one without the other). The runner's two no-trade sentences write decimals invariantly.
- **Item 2 (`1416794b`):** each pass rebuilds its resampler from the deployment's start over every page (`EveryBarSince` kept): a restart mid-hour past 10,000
  minutes equals an uninterrupted run — no code beyond item 1.
- **Item 3 (`01048621`):** the sweep asks `ForwardRuns.CannotRun` (no row; a frozen text that no longer parses or re-identifies — the runner's own pre-step
  question) before any replacement, so it never churns a version the runner cannot run; `CONTRACTS.md` (per minute vs per declared bar),
  `STRATEGY-LANGUAGE.md`, the agents' bars bullet.
- **Judged at landing — TWO TEST NAMES REMOVED, a judged exception (accepted by the orchestrator):** `The_paper_runner_refuses_a_bars_1h_deployment_in_words`
  and `A_run_the_runner_refused_for_its_bars_is_not_replaced_on_the_next_sweep` pinned the refusal this unit had to remove; superseded by (a) and (g).
- **Found, not fixed, now a pre-M0 unit:** a close refused before the wire keeps its request row `CREATED` and the books count it in flight, freezing the run
  — pinned by guard (f) at minute 11; every max-hold close sets it off. Briefed as `U-runner-refused-close` (the orchestrator, 2026-10-04), before M0.

**Verified by running (the builder, quoted; then the manager's gate):** builder at `22d83f4` (on `35606c56`): Release `--no-incremental` 0 warnings, 0
errors; Unit 1333, Fault 415, 0 failed; `ForwardRunnerTests` 16/16, `DeclaredBarsPaperRunnerTests` 4/4, `DeclaredBarsResamplerTests` 15/15, 3× each. RED
before (base `3dff4294`): (a)–(d) "Assert.Null() Failure … this program declares `bars 1h`, and this build's …"; (g) "Expected: [0, 0, 0] Actual: [1, 1, 1]"
against main's guard and against none. Mutants watched red: (i) protection only on declared closes ⇒ (b) "Expected: 2 Actual: 0"; (ii) the cancel only on
declared closes ⇒ (c) "Expected: 0 Actual: -1.000" (a paper short); (iii) the sweep's check removed ⇒ (g); the hold counted in minutes ⇒ (d).
Manager's gate at `7fc9e79`, Release: build `--no-incremental` 0 warnings, 0 errors; Unit 1352/1352 (7 m 28 s, the slow Mac), Fault 415/415 (1 m 41 s), Integration 712/713, 1 skipped (11 m 17 s) → 0 failed.
Names vs `main` (git objects): 2110 → 2117, 2 REMOVED (the two named above — the judged exception; `land.sh check` read FAIL on exactly that and nothing else), 9 added. Scan clean, nothing excluded; no trailers; `rev-list --count` 0 both ways.
**CI:** branch run 37102232929 at `22d83f4` (on `35606c56`; U-invariant-traces and U-venue-verify landed after it, covered by the gate and the merge's run):
success on ubuntu-latest (12 min), macos-latest (15 min), windows-latest (46 min), package (3 min). U-venue-verify's landing push: still running at this record (the ledger and the next record carry it).

**NOT done, NOT verified:** no box run; Integration locally only for `ForwardRunnerTests` (in full on CI and the gate); a version with no execution bounds is
ended at its first intent, not before its first bar (guarding it contradicts `PaperDeploymentTests`); a run's first declared bar is the partial window it
saw from its start (documented); the second finding (a stop/target fill first seen after its minute was live) is `U-runner-refused-close`'s test (c).

## 2026-10-04 — U-inbox-order landed: agent presence is ordered against material passes by the register's own sequence, so no clock step can make a pass attest a window an agent was alive in; every pass the app runs takes the loop's one exclusion

One fresh Opus builder under build-fleet seat P from `docs/briefs/U-inbox-order.md` (briefed `c0ce760` from `U-fix-inbox-boundary`'s two read-only
findings, amended `a1643a6`, dispatched `7ceb7c9`); stopped by the usage limit and by the Mac's sleep and resumed by message; rebased onto `e57bee2`, then
`12f3054`, then at landing onto `4656b12` and `5a54452` (docs only) with the `src`+`tests` patch-id identical (`5c7b1763c4cf`). Merge `87c4dde` (ff-only),
3 commits (2 items + the report), 13 files, +700/−56. No schema rung: the window's mark is kv `material_scan_mark`, beside `material_scan_at`. A `CLAUDE.md`
protection — measurement vs claim, the inbox as data (`Inbox` only across a window with no live agent, `docs/COUNCIL.md` rule 7); no money-path file.

- **Item 1:** `AgentPresence` numbers every window's opening and closing and every pass's start under one lock, and names itself (`Epoch`; a restart is a
  new register); `NoneSince(PresenceMark)` decides — a live agent, one alive at or after the mark, or a mark this register did not issue → no; the scanner
  takes its mark before the walk and stores it with the window; another process's mark, unparsable text, a complete pass that left no mark, or no complete
  pass ever on a database this process did not create (new `Database.CreatedHere`) → unsure, `InboxUnattested`. One injected clock for register and scanner;
  wall time is stamped for people only; the host's scanner asks the shared register.
- **Item 2:** `MissionLoop.TryPass` is the loop's one exclusion; `AppHost.ScanMaterials` (the 30 s tick and the Inbox page's pass) goes through it and
  returns null when refused — a refused tick is owed to the next, a refused page pass says the files will be listed on the next look; `CONTRACTS.md` +1.
- **Judged, the orchestrator's rulings at landing:** DECLARED (1) ACCEPTED — within a process the sequence is an exact order, so a window that a wall-clock
  tie or backward step left unattested (never with an agent in it) now attests: a true claim, with wall time out of the decision as the brief's design rule
  requires, over the letter of its "never yes where it says no today". DECLARED (2) ACCEPTED — a file that arrives while TradeAgent is closed, or before a
  new process's first complete pass, reads `InboxUnattested`, and the Inbox page and the guide now say "the AI may have been running when it appeared".
  DECLARED (3), implementation: the scanner's question is `Func<PresenceMark,bool>`, the register found by delegate identity; no existing test line changed.
- **Manager-read at landing:** the `GatewayPipeServer.cs` change is a comment only; `Database.CreatedHere` is a read-only flag set at migration; no pipe op,
  `state/` file or inbox file can move the sequence.

**Verified by running (the builder, quoted; then the manager's gate):** builder: Release `--no-incremental` 19 projects, 0 warnings, 0 errors; Unit 1340/1340,
Fault 414/414; 3× `MaterialOriginAttestationTests` 12/12, `MaterialPassExclusionTests` 1/1, `CouncilLoopTests` 13/13. RED first, on the base logic plus the
clock seam: (a) `A_backward_clock_step_across_an_agents_window_never_records_its_file_as_the_owners` → "Expected: InboxUnattested / Actual: Inbox"; the
step-back-before-the-agent-starts case; (b) both restart tests; (c) `No_scan_runs_while_a_role_is_launching` → "Expected: null / Actual: seen=8 …"; the
forward and frozen cases green on both sides (guards). Mutant, the comparison back on wall time: 4 failed / 8 passed, (a) red with the same line; restored.
Manager's gate at `e77c143` (the reported tip rebased onto `4656b12`), Release: build `--no-incremental`, 19 projects → 0 warnings, 0 errors; Unit 1360/1360
(7 m 30 s — the local Unit slowdown since the Mac's sleep, measured by the orchestrator; CI unchanged), Fault 415/415 (1 m 40 s), Integration 712/713,
1 skipped (11 m 15 s) → 0 failed; CARRIED to `87c4dde` (only docs moved on `main`). Names vs `main` (git objects): sets 2117 → 2124, 0 removed, 7 added.
Scan clean; no trailers; `rev-list --count` → 0 both ways. **CI:** branch run 37099983439 at `4f670f7`: ubuntu, macos, windows and package success. CI at
the merge: recorded when complete (`fleet/ci-ledger.md`, then the next record).

**NOT done, NOT verified:** no box (CI only); an agent process orphaned by a crashed app is outside any register (a job object contains it on Windows,
nothing does on macOS); a database deleted under a kept workspace attests old files as before; the Inbox page's new line not seen in the running app.
Read-only finding, pre-existing, NOT fixed here → `U-inbox-unreadable` (briefed `54013b6`): `Scan` discards `Collect`'s unreadable-directory count.

## 2026-10-04 — U-runner-refused-close landed: a close refused before the wire no longer freezes a paper run, a refused exit goes out again, and no stop or target rests under no position

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-runner-refused-close.md` (a pre-M0 unit decided by the orchestrator from
U-timeframe-b's finding; briefed from a read-only survey on TB's tip; design accepted 2026-10-04; dispatched `7a7e5d8e`); built on `c15040a5`, rebased by
the manager onto `5ed875ad`, `7068999f` (docs), src+tests patch-id identical. Merge `3958ab57` (ff-only), 7 commits (items 1, 2, 3, 3b, 4, a test fix, the report), 5 files, +656/−13. No schema change.
MONEY PATH, the runner only: `TradingGateway.cs` unchanged; `refused` is terminal only over no request row or one still `CREATED` (never sent, outside the
gateway's open set); an op `dispatched` or UNKNOWN stays in flight and is never re-sent (`CLAUDE.md` rule 3); each re-send is a new op with its own client id.

- **Item 1 (`2f5af7e0`):** the books count an entry, exit or flatten in flight only while it is not refused before the wire. DEVIATION, stricter, accepted:
  out of flight only when `refused` AND the row is absent or still `CREATED` (`RefusedBeforeTheWire`), so a refused op whose row moved on is read off its
  row. Guard (f) re-captured on purpose: m12 `pending=True` → `pending=False`, the minute-12 entry now goes out (its transcript line ends in a deliberate trailing space).
- **Item 2 (`19a36e82`):** a refused exit of a program on declared bars is sent again on the next live minute while the books read long, it is the run's latest
  position-moving op, and its decision is inside both bounds (`DECISION_EXPIRED` still decides at the gateway). Test (d) uses the 14:00 hour (a deviation:
  the evaluator's one-bar hold covers the 13:00 close).
- **Item 3 (`83c9ac9a`):** every live minute whose books read flat cancels each stop or target of the run still working, read off its own ops.
- **Item 3b (`d9aab640`), FOLDED IN on the orchestrator's condition:** the builder read that a paper short FREEZES the run — a sell into a flat book makes −1
  (`PaperBook.cs:510`), the books stay flat, the next entry passes as a reduce, then every close is refused `POSITION_MOVED` (seen on the base by a scratch
  probe, minutes 14–20, never committed) — so every exit (the program's or a re-send) now cancels the run's working stop and target first, as the max hold does.
- **Judged by the orchestrator (2026-10-04):** 3b cancels the stop/target before the exit; if a gate then refuses the exit (quote age, rate limit,
  `DECISION_EXPIRED`, `POSITION_MOVED`, `RISK_LIMIT_EXCEEDED`), the position is unprotected until the re-send, the next exit or the max hold (the kill
  switch, mode and update window refuse the cancel too). ACCEPTED for paper and M0 (a bounded gap, no freeze); NOT for live: U-runner-exit-hygiene adds
  protection put back at once (or exit-first with oversell impossible) and paper realism (a spot sell beyond holdings refused; reduce-only where shorting).
- **Item 4 (`b8ab7b33`), test fix (`56b6ad89`):** `CONTRACTS.md` "The runner"; test (c) stores its minutes unannounced (a queued announcement could settle minute 5
  before the pass), re-verified red on the base code.

**Verified by running (the builder, quoted; then the manager's gate):** builder at `52ad65fb`: Release `--no-incremental` 0 warnings, 0 errors; Unit 1360
(7 m 39 s), Fault 415, 0 failed; `ForwardRunnerTests` (all partials) 22/22 three times. RED before (base): (a) "Assert.False() Failure Expected: False
Actual: True" (m12); (b) "… did not contain any matching items" (no minute-13 entry); (c), (c2), (e) "Expected: 0 Actual: -1.000"; (d) "Expected
["+179#0", "+180#0"] Actual ["+179#0"]". Mutants watched at `52ad65fb`: (i) refused-over-CREATED in flight again ⇒ (a), (b), (f) red; (ii) item 3
removed ⇒ (c2) −1.000; (iii) re-send after any refused exit ⇒ (d) a third exit, refused `POSITION_MOVED`; (iv) 3b's cancel removed ⇒ (e) −1.000.
Manager's gate at `78fd1ab`, carried to `3958ab57`, build tree identical, Release: build `--no-incremental` 0 warnings, 0 errors; Unit 1360/1360 (7 m 41 s, the slow Mac), Fault 415/415 (2 m 2 s), Integration 718/719, 1 skipped (11 m 57 s) → 0 failed.
Names vs `main` (git objects): 2124 → 2130, 0 removed, 6 added. Scan clean, nothing excluded; no trailers; `rev-list --count` 0 both ways.
**CI:** branch run 37169881900 at `52ad65fb` (on `c15040a5`; `main` has moved by docs only since): success on ubuntu-latest (13 min), macos-latest (16
min), windows-latest (48 min), package. U-timeframe-b's landing push `4656b12`: run 37163377166 success; U-venue-verify `c18db14`: 37162165435 success.

**NOT done, NOT verified:** protection put back after an exit refused once its cancel went through (stated NOT claimed in CONTRACTS); an END's refused
flatten and the `TryCreate`→`DISPATCHING` crash window (`U-runner-exit-hygiene`, owed before any live use); the rest of Integration on CI and the gate
only; no box run. M0's last precondition (the orchestrator): this unit.

## 2026-10-04 — U-inbox-unreadable landed: a material pass that could not read everything is not complete — it moves neither window key, marks nothing in that group missing, and says so once on the activity log

One fresh Opus builder under build-fleet seat P from `docs/briefs/U-inbox-unreadable.md` (briefed `54013b6` from `U-inbox-order`'s read-only finding,
amended `17d01f7`, dispatched `c987128`); stopped by the usage limit and resumed by message; rebased onto `5ed875a`, then at landing onto `f149ae0` with the
`src`+`tests` patch-id identical (`582308403598`). Merge `7202e89` (ff-only), 3 commits (2 items + the report), 6 files, +806/−51. No schema rung. A
`CLAUDE.md` protection — measurement vs claim, the inbox as data (`Inbox` only across a window no agent was alive in, `docs/COUNCIL.md` rule 7, which a pass
may shorten only if it read everywhere; no deletion nobody observed); no money-path file.

- **Item 1:** the pass reads the disk only through `WorkspaceReads` (folder there? · list · stat), each answering "absent" only on not-found and throwing on
  anything else; a refused read anywhere makes the pass incomplete — neither window key moves, nothing in that group is marked missing (the other group is
  swept as before), and what it saw is recorded under its unshortened window; `ScanResult` carries `Unreadable`, `UnreadableFolders` (relative, inbox first,
  ≤ 20) and `Complete`; `InboxHoldsUnrecorded` asks the same three reads by the same rule. Skips on purpose stay `Skipped` and hold nothing.
- **Item 2:** `AppHost.RecordWorkspace`, every pass's one way in under the loop's exclusion, says one activity line on change — a warning naming up to three
  folders relative to the workspace (control characters printed as '?', names cut at 80) — and one info line when a complete pass clears it; `CONTRACTS.md` +1.
- **Judged, the orchestrator's rulings at landing — all ACCEPTED:** DECLARED (1), measured on this Mac (.NET 10.0.400): in a folder listable but not
  searchable `FileInfo.Exists` answers false while `Length` throws, so a file the pass found and could not inspect read as GONE — the stat now reads `Length`
  and `LastWriteTimeUtc`, and only not-found means gone. DECLARED (2), the same root, one structural fix: the top-level `Directory.Exists` also answered false on
  a refusal (an inbox whose parent could not be searched read as empty); it is now `FolderIsThere`, which throws. DECLARED (3), judgement: an agent can hold
  the window by making a folder in its own home unreadable — weaker words and late deletions only, never a stronger claim, named on the activity log; a folder
  that vanished after its parent named it counts as unread.

**Verified by running (the builder, quoted; then the manager's gate):** builder: Release `--no-incremental` 19 projects, 0 warnings, 0 errors; Unit 1372/1372
(7 m 38 s), Fault 415/415; 3× `MaterialUnreadableTests` 12/12 and nine neighbouring classes 91/91. RED first, on the base logic plus the listing seam: (a)
`A_folder_unreadable_during_a_pass_never_lets_an_agents_file_in_it_be_recorded_as_the_owners` → "Expected: InboxUnattested / Actual: Inbox"; (b)
`A_pass_that_could_not_list_a_folder_marks_nothing_in_it_missing` → "Expected: null / Actual: 2026-10-04T01:27:39…" (a file stamped removed); (c) the
skipped-on-purpose guard green. On the real disk at base (chmod, macOS) both cases of `A_place_the_disk_refuses_to_describe_is_never_taken_for_gone` stamped the
file removed. Mutant (`Scan` ignoring what it could not read): 8 failed / 2 passed, (a) red with the same line; restored 12/12. Manager's gate at `7202e89`
(the reported tip rebased, 0 behind `main` `f149ae0`), Release: build `--no-incremental`, 19 projects → 0 warnings, 0 errors; Unit 1372/1372 (7 m 42 s, the
local slowdown since the Mac slept), Fault 415/415 (1 m 58 s), Integration 718/719, 1 skipped (12 m 3 s) → 0 failed. Names vs `main` (git objects): sets
2130 → 2143, 0 removed, 13 added (nine tests and four test-disk helpers the heuristic reads as names). Scan clean; no trailers; `rev-list --count` → 0.
**CI:** branch run 37169876739 at `3ba6424`: ubuntu, macos, windows and package success (no midnight crossing). CI at the merge: read by the orchestrator.

**NOT done, NOT verified:** no Windows box — Windows' own refusals are reached only through the seam; the activity line not seen in the running app; a restart
says a standing line once more (state in memory). Read-only finding, pre-existing, NOT fixed → `U-material-file-limit` (owed, light): past `FileLimit`
(5,000) a pass stops at the same files every time, so the rest are never recorded and the window never moves.

## 2026-10-04 — U-data-licence landed at schema 30: every dataset records the terms its bars came under; nothing live rests on research-only evidence; paper goes on

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-data-licence.md` (briefed from a read-only survey on the orchestrator's COMPLY
decision of 2026-10-04 — Binance's archive is research-only — and research R19; dispatched `5a54452b`); built on `c15040a5`, rebased by the manager onto
`a222f539` (U-runner-refused-close and seat P's U-inbox-unreadable in), src+tests patch-id identical. Merge `8ea1ff2e` (ff-only), 6 commits (5 items + the report), 30 files, +1624/−56. **Schema rung 30** (`DatabaseSchemaVersion` 29
→ 30): `data_licence` and four `dataset` columns; all four roll-back sites extended. MONEY PATH: the live gate only REFUSES — never a close, a reduce or a
grant; the promotion's nine hashed facts and its id untouched (the class is read at the gate, so reclassifying is a new reading, not a re-judging).

- **Item 1 (`a897f3c1`):** `data_licence` — a row per READING of a source's terms, append-only, written by rungs only, newest in force; seeded once: the
  Binance archive (`binance-spot-monthly-klines`) `research-only` (Dataset Terms v1.0, updated 2026-08-26, read 2026-10-04); the one backfill gives every
  archive dataset that reading. **DEVIATION on the orchestrator's order:** the forward ledger (`binance-spot-forward-klines`) is seeded `unverified`
  ("R19 § 6 Q2 open: Binance API terms not read for live-gating use"), not `first-party` — it confers nothing; its own red-first assertion in (f).
- **Item 2 (`d8ce9c3b`):** `DatasetRecord.Licence` through `DatasetStore`; the collector stamps a source's newest reading only when every URL's origin is that
  id's BUILT-IN row's (an agent-writable `sources.json` neither sets nor inherits a class); `Rebuild` carries the row's own; only `commercial-ok` and
  `first-party` confer — anything else, NULL included, is research-only ("unknown is never zero").
- **Item 3 (`310aa8ae`):** one pure `DataLicence.LiveRefusal`; `PromotionStanding.LiveRefusal` defaults to a refusal (set on the promoted, paper-eligible and
  refused arms, so `IsPromoted`'s mutant stays seen); `Allocations.Record` refuses after both checks; the live arm is `IsPromoted && LiveRefusal is null`;
  `ALLOCATION_NONE` gives the sentence naming dataset, class and terms; closes, paper and deployments untouched; eight live-path fixtures moved to a
  first-party test source, none removed.
- **Item 4 (`be246b3f`), item 5 (`ec7b34ea`):** the report, the Situation, the promoted line, the readiness blocker, the allocation and Capital-card lines say
  REFUSED FOR LIVE with the sentence; CONTRACTS "Data licences" — **live allocation is closed until a conferring dataset path exists**; the planned route:
  datasets cut from the tape's recordings of a venue whose terms reach own-account use (OKX Europe first), each classed by its venue's reading (a unit
  owed, with or after U-features); NOT claimed: legal advice, R19's Q1/Q2/Q9, the tape's class; USER-GUIDE (backtests and paper go on; the Binance Vision
  credit); RESEARCH-REQUIRED C7.

**Verified by running (the builder, quoted; then the manager's gate):** builder at `1fe12819`: Release `--no-incremental` 0 warnings, 0 errors; Unit
1369/1369 (7 m 47 s), Fault 416/416 (2 m); twelve classes 3×, 62/62 and 24/24. RED before: (a) "capital was allocated to a version promoted on
research-only evidence"; (b) "opening order after : ok — FILLED"; (c) "an allocation went on authorising after its source's newest reading stopped
conferring"; (d) "a promoted version on research-only evidence was given capital"; (e) "… licence reads 'no licence recorded'"; (f) "no such column:
\"licence_class\"". Mutants watched red: (i) the live arm on `IsPromoted` alone ⇒ (b); (ii) the `Record` refusal removed ⇒ (a); (iii) conferring on all but
research-only ⇒ (e); (iv) the newest reading ignored ⇒ (c); (g) with the origin check removed and with a rebuild re-stamping.
Manager's gate at `8ea1ff2`, Release: build `--no-incremental` 0 warnings, 0 errors; Unit 1381/1381 (7 m 45 s, the slow Mac), Fault 416/416 (1 m 55 s), Integration 718/719, 1 skipped (11 m 46 s) → 0 failed.
Names vs `main` (git objects): 2143 → 2153, 0 removed, 10 added. Scan clean, nothing excluded; no trailers; `rev-list --count` 0 both ways.
**CI:** branch run 37169183315 at `1fe12819` (on `c15040a5`; U-runner-refused-close and seat P's U-inbox-unreadable landed after it, covered by the gate
and the merge's run): success on ubuntu-latest (12 min), macos-latest (15 min), windows-latest (38 min), package (4 min). Landing CI read by the orchestrator.

**NOT done, NOT verified:** the Capital card's dashboard line has no test and the app was not run; the tape not seeded (U-features, per venue); a
research-only promotion still opens its `deploy` boundary — seen, not acted on (paper deployments are allowed by design and place nothing live); no box.

## 2026-10-04 — U-fix-loss-reopen landed: one cause behind both Windows loss-watch sightings — the flatten's 2 s budget spent by its own store writes, then never retried — fixed product-side

Built by one fresh Opus fixer under seat P (slot 2, reclaimed from seat A; the second sighting moved it to the front) from `docs/briefs/U-fix-loss-reopen.md`;
rebased onto `e651eaa0` by the fixer, no conflict. Merge `14b5b907` (ff-only), 4 commits (item 1, item 2, the diagnostic's removal, the report), 13 files,
+1038/−39; no schema rung. MONEY PATH: the loss boundary's flatten (`U-flatten-2`) and the reopen's receipt (`LossReopen`); no flatten or reopen condition
loosened (two-pull confirmation, `EligibleAt`); no sleep, test retry or timeout raise; the only re-send is of an attempt whose own record proves nothing went out.

- **Diagnosis (item 1, `d50ce8d3`) — one cause behind both sightings:** windows `c15040a`, run 37166688583 (`LossWatchTests:133`, the day closed, no close sent)
  and windows `3b03041`, run 37098316726 (`LossHoldSurfacesTests:79`, a due reopen did not happen). The flatten ran on the connector's 2 s emergency budget, a
  real wall clock (`RiskReducingScope`, `TickCount64`; every instant otherwise reads `GatewayOptions.Clock`), and its own write-ahead commits were charged to it:
  on a slow disk they spent it before the first platform call, refused before the wire; the outcome was written once, final, with a flagged row refusing the
  sweep — nothing retried, and a day later the reopen was held by "ES 1 open". Runs 37170522369 + 37170524233 (branch-only `LossFlattenDiagTests` and a
  windows-only `build.yml`, both removed in `d0d14159`): each flatten step a ~30 ms commit (first read 1922 ms left, close 1828 ms); 120 instrumented runs and
  2×12 class loops green. **The CONTROL** (deadline gone at the first flatten call) **reproduced both sightings on windows exactly** (`closes=0, mutations 1/1`;
  `reopened=[]`, ES 1). 23 s / 41 s = these fixtures (0.7–4.8 s there) on a disk 5–60× slower: 2–4 commits ≈ 2 s.
- **Item 2 (`15c968c8`, product):** (A) as judgement 1. (B) one `TransportRecord` per attempt: empty ⇒ rows settled not-sent, unflagged (the CANCELLED edge
  `SettleIfNothingWasSent` takes on `NothingWritten`), owed in words (`loss_flatten_owed:` "has NOT closed your open positions yet … tries again on every pass"),
  re-run each pass, `HeldBy` holds the reopen; dispatched anything ⇒ final as before. Docs: CONTRACTS, USER-GUIDE, status schema, AGENTS.md.

**Orchestrator judgements, recorded as judged.** (1) ACCEPTED as a product decision, not a timeout raise: for the app's own flatten, the 2 s emergency budget
measures platform time and excludes the app's own `Database` writes (`RiskReducingScope.BeginExcludingTheStore` + `Core.Db.StoreTime`). The 2 s value, the
owner's presses and the sweeps are unchanged; no test fixture's budget widened. (2) ACCEPTED as a declared gap: a close that MAY have reached the platform, its
answer lost, still never retries and is not confirmed from order history; it stays flagged and paused for the owner, as `U-flatten-2` decided (money rule 3:
ambiguity is never blind re-sent). **OWED, before any live use: `U-flatten-confirm`** — confirm such a close from the platform's order history, only where
`SupportsOrderHistory`, and only then flatten again. (3) OWED in the same unit as (2): `U-flatten-3`'s data-loss exit still charges its own store writes to its
budget — the same root cause; apply change (A) there. (4) ACCEPTED: `LossFlattenTests.A_position_that_shrinks…` had its assertions rewritten, not renamed. Its
protected claim holds, asserted more tightly: never closed at the captured size, the book ends flat, not short.

**Verified by running (the fixer, quoted; then the manager's gate):** RED before: slow-store test `Expected: 1 Actual: 0` ("store held by another : 3050 ms
against a 2000 ms budget"); owed test `Assert.Null() Failure` (final record, `UNKNOWN flagged=1`); every-pass test "1 attempts in all, 1 by the confirming
pass". Mutants watched red: A, the refund removed ⇒ slow-store `Expected: 1 Actual: 0`; B, the empty-record proof removed ⇒ wire guard `Expected: 1 Actual: 2`,
book `[Buy 1 ES FILLED | Sell 1 ES CANCELLED | Sell 1 ES WORKING]`, and 2 `LossFlattenTests` red; both restored identical. Fixer's gate at `d0d14159`: Release
`--no-incremental` 0 warnings, 0 errors; Unit 1381/1381, Fault 420/420; 3× green: LossFlattenOwedTests, LossFlattenTests, LossWatchTests, LossHoldSurfacesTests,
LossFlattenSurfacesTests. Manager's gate at `14b5b907`, Release: build `--no-incremental` 0 warnings, 0 errors; Unit 1381/1381 (7 m 45 s, the slow Mac), Fault
420/420 (2 m 5 s), Integration 718/719, 1 skipped (11 m 54 s) → 0 failed. Names vs `main` (git objects): 2153 → 2157, 0 removed, 4 added (all four in the new
`LossFlattenOwedTests`). Scan clean, nothing excluded; no trailers; `rev-list --count` 0 both ways.
**CI:** branch run 37185798775 at `d0d14159` (the code tip; `14b5b907` adds only the report): success on ubuntu-latest (13 min), macos-latest (16 min),
windows-latest (44 min; Timing first try, per the report), package (3.5 min) — read by the manager with `gh run view`. Landing CI read by the orchestrator.

**NOT done, NOT verified:** judgements 2 and 3 (owed); the Windows evidence is hosted CI only — no box (not granted to this unit; unreachable at its last check); the app not run.

## 2026-10-05 — a second Windows machine for the suite: `TA_WIN_BOX=tests`, and `tools/win-test.sh` runs CI's test job on it, durably

Tooling and docs only: no product or test code changed (`git diff 14b5b907 HEAD -- src tests packaging TradeAgent.sln Directory.Build.props` is empty).
The machine is a Windows 11 laptop (4 cores, 16 GB, French locale) in its own tailnet, shared into the owner's: that one machine crosses, quarantined (it
cannot open a connection back). SSH by a key made for it; no password for it is held anywhere. The ATAS box is unchanged and stays the default machine.

- **`tools/win-env.sh` (`9d263ff4`):** every `win-*.sh` picks its machine by name (`TA_WIN_BOX`; a name with no file is refused, exit 2) and connects by
  key or password through one set of options. `win-bootstrap.ps1` is the one hand step on a new machine: OpenSSH on, port 22 from Tailscale addresses
  only, the key where sshd reads it, no sleep on mains power, `C:\ta`.
- **`tools/win-test.sh` + `win-test-run.ps1`:** a run is a scheduled task (S4U, normal priority) in its own `C:\ta\runs\<id>\`, CI's test job step for
  step; `start` · `status` · `wait` · `log` · `list` · `fetch` · `stop`; one run per machine. `win-ps.sh` now uses one remote file name per call.

**Verified by running.** The ATAS box after the refactor: `win-state.sh` "VERDICT: everything works", a plain command, a 4,000-character script by the
file path, a download, and `TA_WIN_BOX=nosuch` refused with exit 2. The tests box, run `20261005-211439-9d263ff4` (tree `9d263ff4` plus the uncommitted
`tools/` files): **GREEN in 22 m 10 s** — restore 5 s, Release build 69 s with 0 warnings and 0 errors, `Category!=Timing` 711 s, `Category=Timing` 537 s
on the first attempt (no retry). Unit 1380/1380 + 1/1, Fault 415/415 + 5/5, Integration 628/629 + 90/90 with 1 not run — skipped by its own attribute
(`PipeContractTests.A_hello_that_omits_the_version_field_is_read_as_the_current_one`, "Superseded by …") → 2,520 tests, 0 failed: the same counts as the
manager's gate at `14b5b907` (1381, 420, 718/719). Speed probe on the box: cpu 1.55×, file-io 1.95×, pipe 4.44×, timer 1.20×. The run outlived the SSH
session that started it; a second `start` during it was refused (exit 4); `stop` mid-build left no dotnet, testhost, MSBuild or compiler process and the
task Ready; `fetch` brought back the status, every step's log and the six trx files.

**NOT done, NOT verified:** the box has not been restarted under the runner (a Windows Update restart is pending there), so a run across a restart is
untested — by design it would read DIED; a closed lid sleeps the laptop whatever the runner asks; nothing GUI-bound was run there (~~no ATAS~~ — WRONG, corrected in the
next section: its owner's own ATAS and TradeAgent v0.1.0 are installed there; no installer and no app were run; `win-agent`/`win-ui` not tried), and it is not a clean machine (.NET SDK 10.0.400 and Git were already installed); landing CI for this commit not read.

## 2026-10-05 (later) — the owner's rule: when a real machine is available, the real run is the evidence; the tests box joins every unit's gate, and its runner protects its owner's own install

Docs and tooling only; no product or test code changed (`git diff 14b5b907 HEAD -- src tests packaging TradeAgent.sln Directory.Build.props` is empty).

- **The rule, written where work is decided:** `CLAUDE.md` (Building and verifying); `docs/HOW-WE-BUILD.md` — Honesty (a run an available machine could
  have made and nobody made is NOT VERIFIED), pass 1 (the tests box in every unit's gate when `ready` says yes, else "tests box: NOT RUN — <ready's
  answer>"), landing step 3 reads it; still 100 lines; `docs/FLEET.md` (the box, the builder pass, landing); `manager-prompt.md` § 7; the resume block (and
  the ATAS box back online, `R-containment` there and never on the tests box); `docs/DEPLOYMENT.md` § 6; `tools/README.md`. Local, outside git:
  `fleet/BUILDER-PROMPT.md` gate step 5, `fleet/handoff/ORCHESTRATOR.md`, `fleet/BOARD.md`.

- **CORRECTION to the section above, which said "no ATAS":** the tests box carries its owner's own ATAS (installed 2026-06-23) and TradeAgent v0.1.0
  (2026-09-01, with its home and database) — read from `win-state.sh` and the folders' times. The first runs wrote nothing under his home, install or ATAS
  data (0 items written 21:00–22:00).

- **The runner, for a machine with his install on it:** `ready` (0 yes · 1 unreachable · 4 a run in progress · 5 his TradeAgent or ATAS open, a process of
  ours under `C:\ta` excluded), and `start` refuses on 5; per run, `TRADEAGENT_BRIDGE_PIPE` — the tests leave the bridge pipe at the product's
  `TradeAgent.Bridge` (read, not run: five tests build a connector on it and none connects; the one factory call builds Paper) — and the home and gateway
  pipe as backstops; a before/after fingerprint of his three folders, printed by `status`. `win-state.sh` reads a French `quser`.

**Verified by running.** `ready`: "YES" exit 0 when free; "NO - a run is in progress" exit 4 during one; "NO - the machine does not answer" exit 1 against
an address that does not; with a stand-in `TradeAgent.exe` (a copy of `PING.EXE`) running from the user's temp folder, "NO - his own TradeAgent is open"
exit 5, and `start` "REFUSED: his own TradeAgent is open …" exit 5 with no run created; the same stand-in under `C:\ta`, "YES" exit 0; both removed.
`win-state.sh` on the tests box: "Active (id 1, console)", "desktop: live" (was "unknown (id -1)"); the ATAS box unchanged, "VERDICT: everything works".
The runner's own `Fingerprint`, copied verbatim into a scratch check on the box: 0 differences unchanged, 1 after one write, "absent" for a missing folder.
Full run `20261005-215240-db3082c6` with the pipes redirected and the tripwire on (the runner one revision before this commit — since then only the
backstop's status line and the `C:\ta` exclusion, both exercised above and by run `20261005-221457-db3082c6`, "backstop: bridge pipe ta-run-…-bridge"):
**GREEN in 21 m 05 s** — build 59 s, `Category!=Timing` 670 s, `Category=Timing` 527 s first try; 2,520 tests, 0 failed, 1 not run (the same skip); "his
files: unchanged - TradeAgent home 192 files …; TradeAgent install 291 files …; ATAS data 299 files …".

**CI** for `db3082c6` (run 37364863467): macos-latest success (15 min); ubuntu-latest never started — "The job was not acquired by Runner of type hosted
even after multiple attempts" (GitHub's capacity, 0 steps run); windows-latest still running when this was written. This commit's push runs all three
again.

**NOT done, NOT verified:** a run across a restart of the box (it would read DIED); the tripwire against a real change to his files (never provoked — that
is the thing not done); `ready` and `start` with his real TradeAgent or ATAS open (a stand-in only); `stop` on a builder's run; the rule itself is doctrine
until a unit's report carries a box run.

## 2026-10-05 (evening) — the owner's rule: no backward compatibility while we build, written into the code where compatibility gets decided

Docs, comments and the fleet's own files; no behaviour changed — every line added under `src/` and `tests/` is a comment (`git diff -U0 -- src tests`,
non-comment lines: none).

- **The rule** (`CLAUDE.md`, new section "No backward compatibility while we build"): a new build owes nothing to an earlier release — schema and homes,
  files, settings, config formats, wire protocols, the bridge DLL, CLI verbs — and nothing is designed so an older build can read newer data. No migration,
  shim, fallback, dual reader, legacy branch, tolerant old-format parser, deprecated alias or kept-for-old-rows column; existing compatibility code binds
  nothing; a test that exists only for an earlier release may be rewritten or deleted, named in the report; it outranks every older comment, brief, doc and
  test that asks for compatibility. **Not relaxed:** refuse, never guess (another version refused with both versions named; `Versions.BridgeCompatible` the
  model); nothing of the owner's destroyed without his choice in the app (an older home refused untouched; the first refusing change ships the in-app fresh
  start); the money, credential, accounting, evidence and recovery protections for the current build's own data. The phase ends only when the owner ends
  it, in writing, in `CLAUDE.md`.

- **In the code, where the decisions are made:** `Versions` (`src/TradeAgent.Core/Versioning.cs`) carries the code-level statement; the head of
  `DatabaseSchemaVersion`'s history says the 30 rungs' "additive — an older database gains it empty" is a record, not a template; `Database.Migrate` says
  the ladder builds this build's schema and owes earlier releases nothing, only this build's crash recovery; the two undo-list tests (`VenueCatalogTests`,
  `PaperEligibleVerdictTests`) say no new rung owes them a line.

- **In the process:** `docs/HOW-WE-BUILD.md` landing step 4 (the one exception to "nothing removed": a test only an earlier release needed, named; still
  100 lines); `manager-prompt.md` § 7; the resume block (a dated entry; R18's undo lines marked no longer owed). Local, outside git:
  `fleet/BUILDER-PROMPT.md`, `fleet/bin/land.sh`'s judge message, `fleet/handoff/ORCHESTRATOR.md`, `fleet/BOARD.md`.

**Found, not changed — compatibility already in the code, now free to go when it is in the way:** the ladder's 30 additive rungs and their backfills; rows
"an older build wrote" still interpreted (`GatewayTypes.cs:115` and `:133`, `TradingGateway.cs:10059`, `Trading.cs:429`, `PaperBook.cs:177`); the "legacy
frictionless judge" (`VenueCostModel.cs:100`, `Referee.cs`); "a value from a newer build reads as itself" — TEXT, never a CHECK — across nine files (e.g.
`DataLicence.cs:13`, `OrgStore.cs:172`); the two undo-list tests. Also seen: a home NEWER than the build is refused as "TradeAgent's records are damaged.
Press Repair." — the refusal is right, the words are not.

**Verified by running.** `dotnet build TradeAgent.sln -c Release --no-incremental` on the Mac: 19 projects rebuilt, 0 warnings, 0 errors (no project
compiles doc comments as XML, so a comment cannot reach the build). Tests box, run `20261005-224356-08e7b871` (this tree): **GREEN in 17 m 00 s** — build
57 s with 0 warnings and 0 errors, `Category!=Timing` 467 s, `Category=Timing` 487 s first try; 2,520 tests, 0 failed, 1 not run (the attribute skip); "his
files: unchanged". **CI:** `08e7b871` (run 37369028929) ubuntu-latest success, macos-latest success, windows-latest running when this was written;
`db3082c6` (run 37364863467) macos success 14 min, windows success 51 min, ubuntu never acquired a runner (cancelled), package skipped.

**NOT done, NOT verified:** no compatibility code was removed — that is work for the units that meet it, or a sweep the orchestrator may brief; the in-app
fresh start for a refused older home does not exist yet (owed by the first change that refuses one); the "records are damaged" wording for a newer home is
unchanged.

## 2026-10-06 — the vision: `docs/VISION.md`, the end state of the organisation and the law it grows by; docs only

**What happened.** On 2026-10-05 the owner asked, as brainstorming with no production action, to "take a leap and actually think of the best possible
end state of the software where it genuinely becomes an AI organisation". Three passes followed in one session: an end-state brainstorm (the institutions a
hierarchy needs to be a firm — a constitution in pace layers, one governance record, an economy with α-wealth and a contribution ledger, a metabolism, a
proving ground of synthetic markets, beliefs and case law, an immune system); an upgrade drawn from the AI Concierge foundation documents the owner shared
(outside the repo); and an assessment the owner agreed to on 2026-10-06 that kept the upgrade's control-plane primitives (authority lattice, invariant
registry, enforcement classes, simulation boundaries, shadow evaluations, an independent accountability line) and rejected its structural expansion (a
40-seat floor, unlimited active depth, a council in place of the chief) as not earned. Landed: `docs/VISION.md` (the law: representable without limit,
active only when earned); in `docs/ORGANISATION.md` a header note and § 3's bounds made policy data under VISION's earning rule; pointers in `CLAUDE.md` and
the resume block. NO product code, test or build changed; no unit before M-org1 changes; M0 is still next; the last gate figure in this file stands.

**RUN, 2026-10-06:** `git fetch` → `main` = `origin/main` = `76896e1a`, `git rev-list --count HEAD..origin/main` → 0. `git status --short` before this
section was appended → ` M CLAUDE.md`, ` M docs/ORGANISATION.md`, ` M docs/RESUME-HERE.md`, `?? docs/VISION.md` — no `src/`, `tests/` or `tools/` path.
SOURCE, read and not run: rung 28's `org_unit` is a tree by `parent_id` (`Database.cs`) and `OrgStore.Ancestors` / `Subtree` walk a line of any length, so
"representable without limit" needs no schema change for depth.

**NOT VERIFIED:** everything in `docs/VISION.md` is DESIGN, its open questions listed in its § 14 — whether a manager layer pays, whether proving-ground
skill transfers to real edges, whether splitting α-wealth keeps the global guarantee, whether fixed credit splits misallocate, what a model-layer watcher
adds; the AI Concierge documents are cited as design references, not as verified facts about the systems they name. CI: run 37373465478 for `76896e1a` was
cancelled at the owner's instruction on 2026-10-05 with no verdict; this commit's run covers both — pending at the close, for the next manager to record.

## 2026-10-06 — U-tape-events landed: the tape records OKX's official announcements for EU users from first sight, each screened at every read for text addressed to an automated reader

Built by two fresh Opus builders under build-fleet seat A from `docs/briefs/U-tape-events.md` (a CARD from a read-only survey; dispatched `b757bbd3`). The
first builder stopped at the 2026-10-04 throttle with items 1–3 committed and item 4 uncommitted; a fresh second builder re-read and kept 1–3 and finished
4 and 5. Built on `2a12951c`, rebased by the manager onto `11a14999` (docs only between), src+tests patch-id identical. Merge `19d04ab4` (ff-only), 8 commits,
18 files, +1680/−59. No schema change: main schema 30, `tape.db` 1. Not the money path: no gateway, connector, witness, updater, kill switch or approval.

- **Item 1 (`f0f23f6e`, `6707e045`):** the `announcement-json` parser reads the list at a series' `ItemsPath` into one `tape_obs` row per item, keyed by
  the first 32 hex of SHA-256 of its `url` and its own `pTime`; a missing path, id or time, an item over 64 KB, or one key named twice with different
  contents refuses the page in words; an empty list is an empty page; the collector dispatches on the row's parser.
- **Item 2 (`5859a987`, `2dc30cf6`) — DECLARED DEVIATION, accepted:** ONE row, `okx-eea-announcements` (page 1 a minute, a documented ~5-minute delay), its
  terms basis re-read 2026-10-06: OKX API Agreement (28 July 2026) §§ 3.2(a), 9.2–9.4; Terms of Service – EEA (26 May 2026) § 1.14. **Bybit DROPPED:** its
  EU General Terms could not be read on the day (a plain GET 403, WebFetch timed out), so it has no terms basis and no row (EDGE § 2: dropped, not evaded);
  the first builder's § 9.2.2 reading is not re-verified. A file row naming the parser is refused; the vendor scan refuses OKX and Bybit hosts in tests.
- **Item 3 (`bb2adfe3`):** `ClassOf` measures lateness against cadence + the row's documented delay + 30 s (OKX: 390 s live, 391 s archive); a market row
  (delay 0) is classed exactly as before.
- **Item 4 (`8ec5c087`):** `TapeScreen` v1 folds decoded strings through the build's own NFKC table (`Normalize(FormKC)` is the identity under
  InvariantGlobalization on .NET 10.0.11, measured; the 1,986-entry table regenerates from ICU to its pinned hash), case-folds them and drops invisible
  characters; an instruction override, an address to an automated reader, chat-role markup, or any zero-width, bidi or tag character quarantines the item,
  which is still recorded; every read carries `Quarantine` (rule and version, never the text). A quadratic backtrack in the markup rule was fixed (20,000
  `" \n"`: 210 ms → 4 ms a read). **Judged deviation, stricter, accepted:** `AsOf` withholds a quarantined payload from EVERY audience, not only `Pipe` (the
  referee never reads the tape; whether a quarantined item ever reaches a model is `U-annotator`'s decision).
- **Item 5 (`b410dfd3`):** `CONTRACTS.md` "The tape", `USER-GUIDE.md`, `RESEARCH-REQUIRED.md` **C5d** (C5c is U-venue-verify's), the toggle's comment, and
  one sentence on the Market data card (`SettingsView.cs`).
- **Judged at landing — an owner-facing sentence this unit made untrue, owed by the next tape unit:** the Market data card (`SettingsView.cs:289-290`) and
  `USER-GUIDE.md:353` still say a reading is live only "from Binance's own address" and archive from any other; an OKX announcement read on time from
  OKX's own origin is now `O-LIVE`. Folded into `U-tape-archive` at its dispatch (the same card and paragraph); no build the owner runs carries it before then.

**Verified by running (the builder, quoted; then the manager's gate):** builder at `d8bf87a6`: Release `--no-incremental` 0 warnings, 0 errors; Unit 1393,
Fault 420, 0 failed; TapeAnnouncementTests, TapeScreenTests, TapeStoreTests, TapeSourceCatalogTests, SuiteReachesNoVendorTests 3× → 28/0 each. Mutants
watched red, then reverted: (i) the delay dropped ⇒ (c) `TapeStoreTests.cs:305` "Expected: "O-LIVE" Actual: "O-ARCH"" at 390 s; (ii) `AsOf` serving the
payload ⇒ (e) `TapeAnnouncementTests.cs:327` "Assert.Null() Failure"; (iii) the twice-named refusal off ⇒ (b) `:177` "Assert.False() Failure" (2 stored).
Real data (C5d): 15 keyless OKX pages, HTTP 200 in 0.16–0.21 s, 300 items read, 0 flagged.
Manager's gate at `19d04ab4`, Release: build `--no-incremental` 0 warnings, 0 errors; Unit 1393/1393 (7 m 53 s, the slow Mac), Fault 420/420 (1 m 47 s), Integration 718/719, 1 skipped (11 m 16 s) → 0 failed.
Names vs `main` (git objects): 2157 → 2169, 0 removed, 12 added ([Fact]/[Theory] 2113 → 2125). Scan: seven judged false positives, the crypto sense of the word in comments, the screen's benign-word list and test strings, excluded by name ('is a token|this token|as a token|token holders|ONE token|hunters'); no trailers; `rev-list --count` 0 both ways.
**CI:** branch run 37387700466 at `d8bf87a6` (on `2a12951c`; docs-only moves since): success on ubuntu-latest (13 min), macos-latest (24 min), windows-latest
(48 min), package. **Tests box:** NOT RUN — `ready` at 02:13 CEST: "NO - the machine does not answer (…)", as at the builder's ask.

**NOT done, NOT verified:** the card's sentence not seen on screen (no `mac-run`); Bybit's terms; OKX's "~5 minutes" lag against `received_at`; what the
screen misses (an unflagged item is not thereby safe — CONTRACTS says so); completeness (page 1 only, only while the app runs); no box run.

## 2026-10-06 — R-containment landed: a stable AppContainer (C2) confines an AI seat on the owner's Windows build, so `U-contain-seats` builds on it; C1 is benchmark-only, C3 deferred; a credential placed inside is readable, so seats run Topology A

Probe-only leg on the ATAS box, built by one fresh Opus leg under seat P from `docs/briefs/R-containment.md`. Seat P held the `box` lock for the whole leg, from 22:57:59Z
until the report. No product code. Merge `c71dee4e` (ff-only): 4 commits rebased over seat A's `U-tape-events` landing with no conflict; 37 files, +2159.
They are `docs/research/2026-10-06/` (the decision record in the factory plan's § 22 template, 20 per-cell evidence files, `matrix.json`, `run-meta.json`, raw stdout)
and `tools/containment-probe/` (a standalone .NET harness, outside `TradeAgent.sln`, with no credential in it).

- **Decision, for `U-contain-seats` only (`docs/ORGANISATION.md` § 15):** build on **C2**, `CreateProcess` + `SECURITY_CAPABILITIES` AppContainer, measured on the ATAS
  box (Windows 11 Pro 25H2, build 10.0.26200.9457). A seat was kept from TradeAgent's `state/`, the owner's login, `.ssh` and browser profile, host processes and the
  gateway-ACL'd pipe. Persistence (a scheduled task, a Run key) was denied, and egress went only where a capability granted it. Descendants stayed inside, and cancelling
  killed them.
- **Matrix:** C2 17 pass · 0 fail · 2 N/A (cell 10: the broker is not built yet; cell 11: the grant lives in the app) · 1 NOT RUN (cell 14, an npm/pip install inside,
  cut by the leg's box-time bound). C1 (`Experimental_CreateProcess[AsUser]InSandbox`) was checked for availability only: both exports are present and callable, and an
  invalid spec is rejected (err 13). Its matrix was NOT RUN because the valid FlatBuffer spec schema is not public — refused, not guessed — so C1 is benchmark-only.
  C3 (Hyper-V isolation) was NOT RUN: Hyper-V and Windows Sandbox are disabled, and turning either on needs a feature change and a reboot, which is the owner's call. Deferred.
- **The credential question (cell 18):** the owner's login OUTSIDE the container cannot be opened from inside (open-for-read denied, err 5; cells 4a and 18b). A credential
  placed INSIDE the container is readable by code there (18a). So Topology D, the owner's ChatGPT login dropped into the sandbox, is unsafe: seats run Topology A (an
  app-owned harness) or a narrow, disposable token. The codex CLI does not start in a bare AppContainer (18c: it is installed under `%APPDATA%`, which is not granted).
- **The box before and after** (the leg's evidence; not re-run by the manager): `win-state.sh` said "everything works" both times. ATAS is installed, not running, with
  14 strategies, and the installed TradeAgent is unchanged. Created and then removed: `C:\ta\containment-20261006`, the AppContainer profile `TA.RContain.Probe` (hr=0),
  and a window-station/desktop ACE (revoked). Cell 7's persistence attempts were denied, so no task or Run key is left. `C:\ta\repo` and `win-push.sh` were never used.

**Verified by running.** The leg's gate, quoted from its report: (a) `git diff --stat main -- src tests TradeAgent.sln Directory.Build.props` → empty; (b) the
`tools/containment-probe` build on the Mac and on the box → 0 warnings, 0 errors, not in the solution; (c) `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings,
0 errors. The manager's checks at the landed tip: src, tests, sln, `Directory.*.props`, `global.json` and `.github` vs main → empty; the only non-docs files are 9 under
`tools/containment-probe/`; names → removed 0, added 0 (2169/2169). The scan of the whole diff vs main found 31 hits, each judged a false positive and excluded BY NAME:
prose about secrets and tokens, the fake `sk-DUMMY-…` strings and the `OPENAI_API_KEY` field name of the dummy auth file, the `PROBE_SECRET_*` variable names,
`pipeStop.Token`, "lowbox token" and "narrow token". A grep of the diff for host names, IPs, Windows user names and machine SIDs found none.
**Gate CARRIED by judgement (the orchestrator's ruling, 2026-10-06).** Nothing in the solution changes: its build tree is main's byte for byte, and only `docs/` and
`tools/containment-probe/` differ, both outside it. So no local full suite was run, and main's own green carries: `0c510689`, run 37388890179, success on ubuntu, macOS and
windows + package. The landed tree also carries seat A's `U-tape-events`, whose landing CI is seat A's to read.
**CI.** Branch run 37391256380 at `74025d51` (the leg's evidence tip; the final tip adds only the report): ubuntu-latest success (12 m), macos-latest success (25 m),
**windows-latest FAILURE** (36 m), package skipped. The failing test is `PressIdShapeTests.The_operator_cancel_all_names_its_legs_without_the_brokers_order_id`
(`PressIdShapeTests.cs:123`, `Expected: 2 / Actual: 0`; the test took 46 s and the Fault suite 31 m 54 s on that runner). A diff with no change in src or tests cannot
reach it. **A first sighting, recorded as one (the orchestrator's ruling), not called a flake.** It suggests, NOT VERIFIED, the `U-fix-loss-reopen` failure on the OWNER's
cancel-all press. `OperatorCancelAllAsync` opens `RiskReducingScope.Begin` with store time included (`TradingGateway.cs:7709`) and writes its press row before
`GetOrdersAsync` (`:7729`). On a slow disk its own commits could spend the 2 s budget and refuse the read, which is caught as "could not read your working orders": no leg,
nothing cancelled. A second sighting anywhere → a fresh fixer at the front of seat P's queue. Tests box: NOT RUN — `ready`: "the machine does not answer" (switched off).
**NOT done, NOT verified:** C1's matrix; C3; cell 14; the codex CLI inside a container with a granted install (18c shows only that it does not start bare); anything on
the owner's own machine. The decision holds for build 10.0.26200.9457; the revalidation trigger is in the decision record.

## 2026-10-06 — U-test-hygiene-1 landed: the meter tests that went red across midnight now read "today" at the instant they record, and every test process deletes its home when its run ends

One fresh Opus builder under seat P built it from `docs/briefs/U-test-hygiene-1.md`; it batches the orchestrator's `U-fix-midnight-turns` and `U-test-home-cleanup`.
Merge `c2aeb9a4` (ff-only): 3 commits, rebased over R-containment, U-tape-events and docs with an identical src+tests patch-id. Tests only: 15 files, +278/−40.
No product file, no rung, no test removed or renamed. The product's day still turns at LOCAL midnight (`TurnMeter.LocalDay`), and that is unchanged.

- **Item 1 (`54ede058`):** every meter test that records a turn and then reads "today" now pins ONE instant — `TestEnv.LocalNoon()` or the meter's `now` — in the three
  classes run 37163465037 saw red (`TurnRecordTests`, `OwnerPriceTests`, `UnknownModelIsPricedHighTests`). The builder's own grep found the same pattern in
  `TurnAllowanceTests` (2), `AiAttemptLedgerTests` (1), `HarnessBudgetTests`, `HarnessKeyOriginTests`, `VendorLimitTests` and `BudgetReservationTests` (2).
  New GUARD `TurnRecordTests.A_turn_recorded_a_second_before_local_midnight_is_in_that_days_total_and_not_the_next_days`: a turn at 23:59:59 local is in the total read
  0.5 s later and not in the one read at 00:00:01.
- **Item 2 (`03135ee9`) — DECLARED DEVIATION, ACCEPTED (orchestrator and manager):** the brief asked for deletion at process exit. The home is instead deleted at the
  END OF THE ASSEMBLY'S RUN, and again at process exit. The end-of-run step is `tests/Shared/TestHomeFramework.cs`, an xunit 2.9.3 framework step in
  `BeforeTestAssemblyFinishedAsync`, registered for all three test projects in `tests/Directory.Build.props`. The reason, measured: VSTest kills the host
  ~100 ms after asking it to stop, so a 2 s exit handler never finished, while deleting a full Unit home (316 MB, 1,375 files) took 136 ms. Errors are swallowed;
  nothing outside `tradeagent-tests` is deleted; `TA_TEST_KEEP_HOME=1` keeps a home and prints its path once. The four `Loss*SurfacesTests` roots are now inside the home, in a try/finally.

**Verified by running (the builder, quoted from its report):** item 1 on the real clock under a `zic` zone whose local midnight had just passed. At base `2a12951c`
the three classes were 6 of 20 red — the six of run 37163465037 with its figures (2/0, 0.007056/0, 1/0, 0.017256/0, 1/0, 0.056268/0). With the fix: 21/21.
`TurnAllowance` + `AiAttemptLedger`: 3 red at base, 13/13 with the fix. The four classes with a millisecond window were NOT shown red. Guard mutant (`LocalDay` summing
the UTC day) → `Expected: 0 Actual: 1`, `src` restored. Item 2: at base a filtered run left 1 home. A full Unit run with the fix left 0 entries newer than a marker,
and 0 with the exit handler disabled (the end-of-run step alone). The keep run printed its one kept home, which was then deleted by hand.
Builder's gate: Release `--no-incremental` 0 warnings, 0 errors; Unit 1382/1382, Fault 420/420; the 13 touched classes 3×, 93/93 each.
**Manager's gate** at `5baeda5d`, carried to `c2aeb9a4` (only docs moved; build tree identical), Release: build 0 warnings, 0 errors; Unit 1394/1394 (7 m 52 s);
Fault 420/420 (1 m 46 s); Integration 718/719, 1 skipped (11 m 16 s) → 0 failed. Names vs `main`: 2169 → 2170, 0 removed, 1 added (the guard). Scan clean; no trailers.
After the gate, 1 home in `$TMPDIR/tradeagent-tests` was newer than the gate's start. It was born at 04:24:05Z, 4 s after the gate's last suite ended — not attributable to
the gate; whose it is, NOT VERIFIED.
**CI:** branch run 37391490827 at `edb248c1` (code tip): ubuntu ✓ 13 m, macOS ✓ 16 m, windows ✓ 50 m (each Unit 1382, Fault 420, Integration 718 + 1 skipped; Timing
first try), package ✓. That run was dispatched at 23:59:12Z, but its test steps began after 00:00:19Z, so it is NOT evidence of a midnight crossing (said in the report).
Tests box: NOT RUN — `ready`: "the machine does not answer" (both the builder's check and the manager's at landing).
**NOT done, NOT verified:** `RiskGateTests.A_day_past_its_loss_budget_refuses_…` (Fault) has the same fault on the UTC day, through `FakeBroker`'s own `UtcNow` (`:146`)
with no seam; fixing it needs a product change, so it is OWED to seat P as a light unit. Leftover homes on Windows (files held by a child process) were not measured.
A host killed mid-run still leaves its home to `fleet/bin/purge-test-homes.sh`. An IDE reusing one host for two runs is not handled.

## 2026-10-06 — U-runner-exit-hygiene-a landed: an END whose close a gate refused before the wire stays owed and goes out again, and an order stopped between its record and the wire is over, not a run frozen for good

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-runner-exit-hygiene-a.md` (briefed from a read-only survey at `2a12951c`, split
in two on the orchestrator's ruling; queued `200acb4c`, dispatched `11a14999`); rebased by the builder onto `69589886`, then by the manager onto `cdb5df85` (U-test-hygiene-1, test infrastructure), src+tests patch-id identical. Merge `fe6a95fd` (ff-only), 4
commits (3 items + the report), 9 files, +830/−115. No schema change. MONEY PATH: the gateway's deployment ledger and the runner's predicate; every gate
unchanged; `CLAUDE.md` rule 3 kept — only what provably never left (`dispatched_at` null; `DISPATCHING` is durable before the wire) is settled or sent again.

- **Item 1 (`20b59e1a`):** ONE predicate, `Deployments.RefusedBeforeTheWire` (`refused` over no row or a row never `DISPATCHING`; `NeverReachedTheWire` its
  row half), read by the runner's books, its exit-sent-again rule and the gateway's END. `CloseAsync`'s null now RESOLVES the flatten ("there was nothing to
  close"). An ended run whose latest flatten was refused before the wire, or that has none, is OWED: each reconcile pass cancels what of it works and closes
  under a new op, at most once a minute, writing nothing while `TryAuthorizeExecution` refuses its identity or on a moved platform, mode or account; it holds
  its slot; its line and `status.deployments` say ENDED, NOT closed, the blocker or last refusal (code first), retried each minute, no replacement; the Safety
  page lists by the same `DeploymentReading.Outstanding`.
- **Declared deviations, each stricter and tested, ACCEPTED by the orchestrator (2026-10-06):** (a) the slot is counted on platform, account and instrument
  (`Deployments.OnInstrument`), not the envelope alone — else a re-granted envelope starts a run over an owed close; (b) an owed close waits while another run
  on that account and instrument is not over or has an unsettled op (`AnotherRunOnItsPosition`); (c) `NeverReachedTheWire` excludes `AWAITING_APPROVAL` (an
  approval can still send it).
- **Item 2 (`5581157f`):** an op `dispatched` over a row still `CREATED`, or over none, past `DispatchStrandedAfter` (gateway clock, from the row's or op's
  `created_at`) is settled BY THE STORE in both passes: `CREATED → CANCELLED` by `Transition`'s CAS ("nothing was sent: …"; with no row, a `CREATED` row goes
  under the id first) and only the CAS winner refuses the op, so a dispatcher still alive sends nothing; inside the bound nothing happens (a no-row op is no
  longer refused on sight); a terminal row that never reached the wire reads `refused`, never `resolved`.
- **Item 3 (`8c613484`):** `CONTRACTS.md` "The runner" states both former NOT-claimed lines as claims, each with what it still leaves; `USER-GUIDE.md`.
- **For the owner, as the END already was:** an owed close takes the ACCOUNT's whole position in that instrument, so a position of his own there goes with it.
  ENDs an earlier build wrote over a flat book now read owed: the first pass calls that close once — flat, it resolves (no shim, the owner's 2026-10-05 rule).

**Verified by running (the builder, quoted; then the manager's gate):** builder at `ae76b660`: Release `--no-incremental` 0 warnings, 0 errors; Unit 1393,
Fault 425, 0 failed; `PaperDeploymentTests` 3× 11/11; Integration `ForwardRunnerTests`+`SweepRequestIdTests` 66/66 before its rebase. RED before (base
`11a14999`): (a) "Expected: 0 Actual: 1" — "while owed: started 1", status "not listed", position 1, no second flatten; (c) "Expected: 2 Actual: 1". On the
item-1 tip: (b) "Expected: 0 Actual: 1" — past the bound "dispatched", row `CREATED`, released "orders at the wire: 1"; before its row "Expected:
"dispatched" Actual: "refused"". Mutants watched red: (i) the slot free once every op settles ⇒ (a) "Expected: 0 Actual: 1"; (ii) the settle refuses the op
and leaves the row `CREATED` ⇒ (b) "orders at the wire: 1"; the deviations against (d): per-envelope count, no wait ⇒ each "Expected: 1 Actual: 2".
Manager's gate at `fe6a95fd`, Release: build `--no-incremental` 0 warnings, 0 errors; Unit 1394/1394 (7 m 53 s, the slow Mac), Fault 425/425 (1 m 46 s), Integration 718/719, 1 skipped (11 m 15 s) → 0 failed — on the tree with U-test-hygiene-1, which the branch run predates.
Names vs `main` (git objects): 2170 → 2174, 0 removed, 4 added ([Fact]/[Theory] 2126 → 2130). Scan clean, nothing excluded; no trailers; `rev-list --count` 0 both ways.
**CI:** branch run 37395589500 at `ae76b660` (on `69589886`): success on ubuntu-latest (12 min), macos-latest (24 min), windows-latest (41 min), package.
U-tape-events' landing push `71c8a2b0`: run 37395067722 success on all three and package. **Tests box:** NOT RUN — `ready` at 06:26 CEST: "NO - the machine does not answer (…)".

**NOT done, NOT verified:** two END callers at once can each send a close (base `TradingGateway.cs:963`, `:989`) — OWED, a probe then a unit, before any live
use, after -b; a close that MAY have reached the wire is never sent again here (seat P's `U-flatten-confirm`); a dispatch slower than the bound is settled under
it and sends nothing (a missed order, never a double); the Safety-page list and the new lines were built, not seen in the running app; no box run.

## 2026-10-06 — U-flatten-confirm landed: a loss-budget close whose answer was lost is now settled from the platform's order history when that history finds it, and the book is closed again only once every leg is decided; the data-loss exit no longer charges its own store writes to its budget

One fresh Opus builder under seat P built it from `docs/briefs/U-flatten-confirm.md`, written by seat P's survey. "Absence as proof" was split off at dispatch as `U-flatten-absence`.
Merge `f461b19f` (ff-only): 3 commits, rebased over U-test-hygiene-1, U-runner-exit-hygiene-a and docs with an identical src+tests patch-id; 10 files, +1356/−63; no rung.
MONEY PATH: the loss boundary's flatten (`U-flatten-2`), the data-loss exit (`U-flatten-3`) and the health pass. This is `U-fix-loss-reopen`'s owed judgements 2–3.
The orchestrator ruled on the survey's decisions 1–8 on the owner's behalf: if the book is still open once every leg is decided, it is closed again, not left for the owner.

- **Item 1 (`da11b2b2`):** `CancelWhileUnvaluableAsync` and `ExitLostValuationAsync` open `BeginExcludingTheStore`, so the data-loss exit's budget measures platform time.
  `RiskReducingScope`'s doc and CONTRACTS say so.
- **Item 2 (`7d8e1205`):** `ConfirmLostClosesAsync` runs at the start of each health pass (after the account read, before the execution row and the loss watch), and only
  where `ReconciliationProvable`. For each lost close leg it asks `ReconcileAsync`'s two questions over `ReconcileAsync`'s window: is our id in the history, then are
  there fills. It never decides from absence. A leg found working or not found stays undecided.
  When every leg is decided, ONE write-once `loss_flatten_confirm:` record (the verdicts and a fresh read-back of the book) is written BEFORE any row is settled. Then
  `SettleTheUnresolved`'s two steps run and the outcome's nonces are unflagged. A flat book is confirmed and nothing is sent. A book still open is closed by the same
  `FlattenForBreachAsync` under a fresh nonce, with its outcome in `loss_flatten_again:` / `…_owed:`. One confirm per breach. `LatestFlattenWord` now feeds `HeldBy`,
  `FlattenStateToday` and `FlattenFlagFor`; CONTRACTS, the status schema, AGENTS.md and a USER-GUIDE paragraph say so.
- **Declared deviations, ACCEPTED (orchestrator):** (a) when the day's flatten already closed a symbol, that symbol's "closing again" leaves no word of its own; otherwise
  a false "closing again" would hold the closure for ever. Its own test went red with that hunk alone (`Expected: "flat" Actual: "unresolved"`). (b) `SettleTheUnresolved`
  takes only the second step for a row already RECONCILING (a confirm killed mid-settle); every other caller is unchanged. (c) "Only UNKNOWN close legs unresolved" is read
  strictly: a non-final cancel-half row, or a lost leg the owner has since settled, means no confirm, and that hold stays (`U-loss-hold-release`, owed). (d) A USER-GUIDE paragraph.

**Verified by running (the builder, quoted).** RED before, with the item reverted:
- (vi) `Expected: 1 Actual: 0` closes, "store held by another : 3004 ms against a 2000 ms budget";
- (i) `Expected: FILLED Actual: UNKNOWN`; (v) both arms `Expected: FILLED Actual: UNKNOWN` after `FillWorking`;
- (viii) `Expected: REJECTED Actual: UNKNOWN`, closes 1, ES 1; the one-confirm test the same.
Guards green at base and after, unchanged: (ii)–(iv), `LossFlattenTests.cs:439`, `LossFlattenOwedTests.cs:372`, `LossReopenTests.cs:194`, `LossReleaseTests.cs:128`.
Mutant `DecidesALostClose(s) => IsTerminal(s) || s == WORKING` → (v)[LeaveWorking] `Expected: 1 Actual: 2` closes ("Sell 2 CANCELLED", cancels 0 → 1); restored, sha256 equal.
Builder's gate at `ac62ca92`: Release 0 warnings; Unit 1393/1393, Fault 430/430; `LossFlattenConfirmTests` 3× 10/10; five neighbouring loss classes 3× 28/28 (before the rebase).
**Manager's gate** at `ec99474e`, carried to `f461b19f` (only docs moved; build tree identical), Release: build 0 warnings, 0 errors; Unit 1394/1394 (7 m 52 s);
Fault 435/435 (1 m 49 s); Integration 718/719, 1 skipped (11 m 15 s) → 0 failed. Names vs `main`: 2174 → 2182, 0 removed, 8 added (7 tests and the helper `Seam`). Scan clean; no trailers.
**CI:** branch run 37396439616 at `ac62ca92` (the code tip): ubuntu ✓ 12 m, macOS ✓ 16 m, windows ✓ 52 m, package ✓ (read by the manager with `gh run view`).
Tests box: NOT RUN — "the machine does not answer" (the builder's check and the manager's at landing). Landing CI: a waiter is armed.
**NOT done, NOT verified:** ATAS (no box). That its lost closes mostly stay undecided is read from the adapter's code, not run. No test kills a pass between the
confirm record and the settle, or fails the confirm write. Owed separately: absence as proof (`U-flatten-absence`), the data-loss exit's own lost close
(`U-valuation-close-confirm`) and a closure held after the owner settles a lost leg (`U-loss-hold-release`). The app was not run.

## 2026-10-06 — U-tape-archive landed: the tape records GDELT's crypto news items from its fifteen-minute files with GDELT's own first-seen time — live when seen in time, point-in-time when fetched late from a file its checksum proves — under a 25 MB daily cap, keeping no raw bytes

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-tape-archive.md` (a CARD from a read-only survey; amended at dispatch against
U-tape-events' landing, `6b312430`; dispatched `0aba7279`); built on `cdb5df85`, rebased by the manager onto ``2952c285``, src+tests patch-id identical. Merge
`90f74555` (ff-only), 6 commits (5 items + the report), 21 files, +2999/−86. No schema change: main 30, `tape.db` 1. Not the money path. Binance's archive stays
dropped (its Dataset Terms; no request to its host).

- **Item 1 (`18bd446b`):** `GdeltGkg` reads the GKG files: 15-minute labels; addresses built from a label on GDELT's own host; a listing line read for size, MD5
  and label only; the zip streamed through MD5, SHA-256 and the inflater; every row checked against its label; filter `gkg-crypto-v1`; the 27 fields whole,
  ≤ 64 KB. **Deviation, accepted:** a row the filter does NOT keep is read leniently (2 of 24 real files held a raw Latin-1 byte, so strict UTF-8 refused
  them whole); a KEPT row that is not UTF-8 still refuses its file.
- **Item 2 (`54a5c93c`):** `TapeStore.AppendArchive` — one transaction, refusals before it, the class rule there only: `O-LIVE` in time; else `O-PIT` iff own
  origin, MD5s equal and Last-Modified ≤ label; else `O-ARCH`; never upgraded; a re-read writes nothing. **Deviation, accepted:** the `Archives()` catalogue
  row lands here (the rule reads it); `TapeCollector` skips its parser's row; `TapeSourceCatalogTests`' shipped list and counts updated (5+1 → 5+1+1).
- **Item 3 (`cc08cfbb`):** `GdeltRecorder` on its own task: a look at each label + 2 s; backfill ≤ 96 files a start, ≤ 7 days, ≤ 500 MB a UTC day, 2 s apart, live
  backoff; nothing written before the MD5 matches; a 25 MB cap per label-day that stops that day and says so once. **Screen fix, accepted:** GDELT writes
  titles with XML character references, which screen v1 read raw (`&#x200B;` and escaped chat-role markup passed); `TapeScreen` v2 resolves them first
  (`TapeScreenTests`' literal version 1 became `TapeScreen.Version`).
- **Item 4 (`a10492e7`):** "Record GDELT news", the owner's second one-press switch on the Market data card, ON by default under the orchestrator's bound of
  2026-10-03 (measured 6.88 MB a day kept against the 25 MB cap; raw bytes never kept); `AppHost` starts the recorder beside the tape and stops it first.
- **Item 5 (`9f09b1ed`):** `CONTRACTS.md` "The tape", `USER-GUIDE.md`, `RESEARCH-REQUIRED.md` **C5e**; the live/archive sentence U-tape-events left naming
  Binance alone now names each source's own address, on the card and in the guide — the debt in U-tape-events' record is paid.
- **GDELT's terms (re-read 2026-10-06 00:42Z): any use, commercial included, if the GDELT Project is credited with a link.** Checked at landing, the
  orchestrator's condition: the Market data card (`SettingsView.cs:305-306`) and `USER-GUIDE.md:370` credit "the GDELT Project
  (https://www.gdeltproject.org/)"; the row carries the same `Citation` for every later surface (`U-tape-read` must show it).

**Verified by running (the builder, quoted; then the manager's gate):** builder at `c5cf36c6`: Release `--no-incremental` 0 warnings, 0 errors; Unit 1410,
Fault 420, 0 failed; nine tape classes 3× → 48/0 each. Mutants watched red: (i) Last-Modified ≤ label dropped ⇒ `TapeArchiveStoreTests.cs:81` "Expected
"O-ARCH" Actual "O-PIT""; (ii) rows before the MD5 ⇒ `GdeltRecorderTests.cs:93`; (iii) the 7-day bound removed ⇒ `:182`, the plan held 2026-09-29T00:45;
(iv) the daily cap removed ⇒ `:374` "Expected [3, 0, 0, 3] Actual [3, 3, 3, 3]". Measured (C5e): `lastupdate.txt` 200 in 0.83 s; 24 hourly files streamed,
none kept: 419 MB a day on the wire, 99 of 24,671 rows kept; MD5 = storage header = ETag 24/24; files published 569–724 s before their label; 0 of 99 flagged.
Manager's gate at `90f74555`, Release: build `--no-incremental` 0 warnings, 0 errors; Unit 1410/1410 (8 m 27 s, the slow Mac), Fault 435/435 (1 m 51 s), Integration 718/719, 1 skipped (11 m 15 s) → 0 failed — on the tree with U-runner-exit-hygiene-a, U-test-hygiene-1 and U-flatten-confirm, which the branch run predates.
Names vs `main` (git objects): 2182 → 2199, 0 removed, 17 added (16 tests + the `PublishFile` helper; [Fact]/[Theory] 2137 → 2153). Scan: seven judged false positives, a CancellationTokenSource's `.Token`, excluded by name (`leash`, `_stopping`); no trailers; `rev-list --count` 0 both ways.
**CI:** branch run 37414286696 at `c5cf36c6` (on `cdb5df85`): success on ubuntu-latest (13 min), macos-latest (16 min), windows-latest (39 min), package.
U-runner-exit-hygiene-a's landing push `a76a30ed`: run 37415393690 success on all three and package. **Tests box:** NOT RUN — `ready` at 07:25 CEST: "NO - the machine does not answer (…)".

**NOT done, NOT verified:** the recorder never ran against GDELT itself (only `GdeltGkg`, through the measurement harness), so no real `O-LIVE` or `O-PIT`
row has been seen — the classes are proven at the store; the card not seen on screen; the filter's recall; a restart on a capped day may read one more file.

## 2026-10-06 — U-runner-exit-hygiene-b landed: an exit a gate refused before the wire puts the run's stop and target back on that same minute, and the paper book trades as the spot account it simulates — never short

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-runner-exit-hygiene-b.md` (the second half of seat A's survey at `2a12951c`;
queued `200acb4c`; amended at dispatch against -a's landing, `fde82e34`; dispatched `3d7b566c`); rebased by the builder onto `21ba16b1`, then by the
manager onto `a562283a` (docs only between), src+tests patch-id identical. Merge `5f03e81c` (ff-only), 4 commits (3 items + the report), 8 files, +648/−32. No
schema change. MONEY PATH: the runner and the paper connector; `TradingGateway.cs` untouched; nothing refused is re-sent here (rule 3) — the protection put
back is new orders under that minute's own ids, through every gate, the stale-close read included.

- **Item 3 (`cb6866b1`):** `ForwardRuns.PutProtectionBackAsync`, last on each live minute, both clocks: while the books, re-read afresh, read long, the run's latest
  entry, exit or flatten is an exit refused before the wire by `Deployments.RefusedBeforeTheWire` (-a's one predicate, reused) and the maximum hold is not
  reached, the latest stop and target since the position's entry go back at their own levels, sized from the books, through `RestAsync` — on the refusal's
  own minute, never while an exit is in flight. **Design (a), ruled by the orchestrator: put back at once, not exit-first** (on the Simulator and ATAS a
  sell past the position is a short; the SDK has no reduce-only). **Declared deviations, accepted:** (a) PER KIND — the stop goes back while no stop of the
  run may be working (UNKNOWN counts as working), the target likewise, so a half-refused re-place is asked again; (b) a cheap pre-check (the run wrote an
  exit and the minute opened long) before the ledger read, so a long catch-up reads nothing more for a run that never exited.
- **DECLARED GAPS of design (a), the orchestrator's ruling:** (1) the position is unprotected from the exit's cancel to the re-place — on paper the minute
  in progress, an order being judged from the next bar's open; (2) a gate refusing the re-place too (the rate limit, `POSITION_MOVED`) leaves it unprotected,
  asked again each live minute, a refused row per order meanwhile; (3) the kill switch, mode or update window engaged between the cancel and the exit refuse
  the re-place too — unprotected until they lift (engaged before the cancel, they refuse the cancel and keep the protection).
- **Item 4 (`a01bfbe8`):** `PaperConnector` refuses a sell beyond `PaperBook.HoldingAndWorkingBuys` ("insufficient holdings", a definite refusal); `TryFill`
  re-reads the holding inside its own transaction and rolls a sell fill beyond it back to a savepoint, the order `REJECTED`, unfilled. The agent's own paper
  sells beyond the holding are refused too, by design. Reduce-only as a GUARD, (f): every sell the runner or its END sends carries `OrderIntent.Close`, every
  buy `Open`; the connector half is DEFERRED to the ATAS box — owed before the runner reaches a venue that shorts, or live.
- **Item 5 (`612dc9c1`):** `CONTRACTS.md` "The runner" (the put-back, its three gaps, why not exit-first, the close intent as the reduce-only hook) and "The paper
  connector" (never short; NOT claimed: no holding lock — two sells on one holding need an OCO on a real spot venue; buys not held to cash); `USER-GUIDE.md`.
- **Rewritten, not renamed:** `PaperSettlementTests.A_stop_fills_at_the_open_on_a_gap_a_target_at_its_level_and_both_touched_is_the_stop` sold into an empty
  book — each half now buys its unit first, assertions unchanged; no other test did (`LossFlatten*Tests` run on the Simulator, untouched).

**Verified by running (the builder, quoted; then the manager's gate):** builder at `208675e1`: Release `--no-incremental` 0 warnings, 0 errors; Unit 1410,
Fault 436, 0 failed; `ForwardRunnerTests` 3× 25/25, `PaperSettlementTests` 3× 11/11. RED before (src stashed on `2952c285`): (c) "Assert.Single() Failure:
The collection did not contain any matching items" — exit refused `DECISION_EXPIRED`, both cancels `CANCELLED`, the account `Long/1.000`, nothing working;
(e) "Assert.Throws() Failure: No exception was thrown". Mutants watched red: (iii) the refused-exit condition dropped ⇒ (d) "Assert.Empty() Failure:
Collection was not empty" (stop and target WORKING beside the exit in flight); (iv) the fill-time check removed ⇒ (e) "Expected: 0 Actual: -1.000".
Manager's gate at `12882023` (carried to `5f03e81c`: build tree identical, docs only between), Release: build `--no-incremental` 0 warnings, 0 errors; Unit 1410/1410 (8 m 29 s, the slow Mac), Fault 436/436 (1 m 49 s), Integration 721/722, 1 skipped (11 m 13 s) → 0 failed.
Names vs `main` (git objects): 2199 → 2203, 0 removed, 4 added ([Fact]/[Theory] 2153 → 2157). Scan clean, nothing excluded; no trailers; `rev-list --count` 0 both ways.
**CI:** branch run 37420566289 at `208675e1` (on `21ba16b1`; docs only since): success on ubuntu-latest (12 min), macos-latest (24 min), windows-latest (53 min),
package. U-tape-archive's landing push `d89f9cdc`: run 37420286516 success on all three and package. **Tests box:** NOT RUN — `ready` at 11:04 CEST: "NO - the machine does not answer (…)".

**NOT done, NOT verified:** connector-side reduce-only on the Simulator and ATAS (the box's question); how often a catch-up re-places the pair while its exit
keeps being refused; the put-back and the paper refusal seen in the running app; no box run.

## 2026-10-06 — U-fix-bridge-heartbeat landed: the macOS red was the runner, not the bridge — 3,639 measured executions never lost a pulse, so the test moves to the Timing step with its asserts unchanged

Built by one fresh Opus fixer under seat P from `docs/briefs/U-fix-bridge-heartbeat.md` (the red: main `5427746`, run 37058319403, macos-latest only,
`BridgeRoundTripTests.A_failing_capability_read_does_not_stop_the_heartbeat`, "Expected: True / Actual: False", first sighting).
Merge `313058e3` (ff-only), 5 commits: two diagnostic rounds, their removal, item 2 and the report, rebased over seat A's landings and docs with an identical
src+tests patch-id. The net diff vs `main`: one `[Trait("Category","Timing")]` and 19 comment lines in `BridgeRoundTripTests.cs`. No product code, no rung, no assertion,
timeout, interval or delay changed.

- **Item 1 — the cause is the RUNNER (measured).** Six instrumented macos-latest runs (37413639706, 37413642258, 37413644218, 37415665297, 37415667586, 37415669858)
  printed every pulse's send and receipt beside three canaries. 3,639 executions and 44,675 pulses with `Describe()` throwing showed 0 connections lost, 0 loop exits and
  no pulse lost. The worst gap per execution was p50 166 ms and p99 189 ms (the VM wakes every timed wait up to ~90 ms late, a dedicated thread as much as `Task.Delay`).
  Four gaps passed 300 ms; the worst was 511 ms, when the VM stopped running the process (38 ms of CPU in 1.6 s).
- **Declared deviation, ACCEPTED by the manager:** the natural red never recurred, so the picture around the failing assert comes from an injected 750 ms SIGSTOP.
  It reproduced the red in all 36 stalls, with the loop still running throughout. The original red's own gap cannot be recovered.
- **Item 2 — `Timing` membership argued at the test with those numbers.** The Timing step runs the same test with the same asserts and deadlines, no tolerance. On a
  failure it writes the names to the job summary, annotates the run, keeps the trx and re-runs the category ONCE, which decides; red twice in a row is a red run.
  **Judged by the manager:** moved to Timing on 3,639 measured executions; an intermittent product stall rescued once per run stays possible, and it is annotated —
  none was seen in 44,675 pulses.

**Verified by running (the fixer, quoted).** Mutant (`Describe()` unguarded, its throw ending the loop): red on two runs in a row, `Assert.True() Failure Expected: True
Actual: False` at `:342`; restored, green. `Category=Timing` selects the test and `Category!=Timing` does not; both were run.
Fixer's gate at `0ffc1b3d`: Release 0 warnings, 0 errors; Unit 1410/1410, Fault 435/435; `BridgeRoundTripTests` 3×, 41 each.
**Manager's gate** at `bccff30d`, carried to `313058e3` (only docs moved; build tree identical), Release: build 0 warnings; Unit 1410/1410 (8 m 27 s); Fault 436/436
(1 m 48 s); Integration 721/722, 1 skipped (11 m 13 s) → 0 failed. Names vs `main`: 2203/2203, 0 removed, 0 added. Scan clean; no trailers.
**CI:** branch run 37420396771 at `0ffc1b3d`: ubuntu ✓ 12 m, macOS ✓ 25 m, windows ✓ 52 m, package ✓; this test passed first time on all three. That run's macOS Timing
first attempt failed ANOTHER test: `SweepRequestIdTests.A_sweep_pays_the_emergency_budget_once_not_once_per_rpc`, NullReferenceException at `:333` (a CancelAll reply with no
Data), green on its re-run. A diff of a trait and comments cannot reach it. It is recorded as a sighting in `fleet/ci-ledger.md` and was passed to `U-fix-press-budget`'s fixer.
Tests box: NOT RUN — "the machine does not answer" (the fixer's check and the manager's at landing).
**NOT done, NOT verified:** what stalls the macOS VM (host-side); no Windows hardware run. Outside the brief, found by the fixer and now OWED before any live use as
`U-bridge-liveness-clock` (queued `a562283a`): liveness reads `DateTimeOffset.UtcNow` (`AtasConnector.cs:557`, `:1760`) while the write and answer deadlines read
`TickCount64`, so a backward clock step would keep a silent bridge READY.

## 2026-10-06 — U-flatten-absence landed: where a connector's closes provably carry our client id, a lost loss-budget close that a complete history never saw is settled as never sent and the book is closed again; ATAS never claims it

Built by one fresh Opus builder under seat P from `docs/briefs/U-flatten-absence.md`: the survey's item 3, split off from `U-flatten-confirm` at its dispatch, with
pointers re-checked against that landing (`abcb758a`). Merge `f65c4ba8` (ff-only), 2 commits, rebased over U-fix-bridge-heartbeat and docs with an identical src+tests
patch-id; 15 files, +417/−43; no rung. MONEY PATH: this is the one place where absence becomes proof and a second close goes on the wire.

- **Item (`56408b19`):** `ConnectorCapabilities.ClosesCarryClientOrderId` (init-only, default `false`). The simulator and TradeAgent paper claim it, each with its own test
  that its close and the close's fill carry the given id (`LossFlattenConfirmTests.The_simulators_close_carries_the_id_it_is_handed_onto_the_order_and_its_fill`,
  `PaperConnectorTests.A_close_carries_the_id_it_is_handed_onto_the_order_and_its_fill`). ATAS never claims it, and its `Capabilities` doc says why: ATAS builds the close
  itself, and our id goes on only as a best-effort label afterwards (a filled close measured on the box bore ATAS's own "Close position"). That ATAS change is doc-only.
  The rule sits in the confirm's history question (`AskTheHistoryAsync`), behind `AbsenceDecidesALostClose` = `ReconciliationProvable && ClosesCarryClientOrderId`. If both
  `since` reads answered with no order and no fill under the leg's id, past `AbsenceGrace` from `AbsenceCountsFrom`, the leg is CANCELLED, "it never reached the platform",
  and the confirm proceeds as for any decided leg. `SupportsClientOrderId`, `ReconcileAsync` and the rest of `U-flatten-confirm` are untouched. CONTRACTS and the SDK's docs say so.
- **The one assertion flipped, by design:** the `"plain"` case of `A_lost_close_the_platform_never_saw_decides_nothing_past_the_grace` left that theory. It is now (ii),
  `A_lost_close_the_platform_never_saw_is_settled_as_never_sent_past_the_grace_and_closed_again_once`. Its old claim lives on as (vii), the theory's new case "closes do not
  carry the id". (iii) "history hidden" and (iv) "history read throws" are unchanged. The theory's NAME stays, so no test name was removed.
- **Declared deviations, ACCEPTED by the manager:**
  (a) The mutant bites (iv), not (iii): `HideOrderHistory` withdraws `ReconciliationProvable`, so `U-flatten-confirm`'s gate returns before any history read and no mutant
  of this guard can reach (iii). That is a correct finding about where the guard bites.
  (b) USER-GUIDE, the status schema's `loss_flatten` text and AGENTS.md said that a close the history cannot find settles nothing; they are corrected (paper and the simulator settle it, ATAS does not).
  (c) Added `CapabilityTests.A_close_carries_the_client_id_only_where_a_connector_says_so` (the default stays false) and the `RecordingConnector.ClosesCarryTheId` knob.

**Verified by running (the builder, quoted).** RED before (product stashed, test only): (ii) `Assert.Equal() Failure: Expected: CANCELLED Actual: UNKNOWN`
(`LossFlattenConfirmTests.cs:359`), its inside-the-grace half passing. The guards at base, 3/3, passed.
Mutant "a history read that threw read as absent" → (iv) `Expected: 1 Actual: 2` closes, "lost close: CANCELLED flagged=False", a second close on the wire; (ii), (iii)
and (vii) green under it; restored, sha256 equal.
Builder's gate at `0c260181`: Release 0 warnings; Unit 1411/1411; Fault 439/439; touched classes 3×, 15/15 and 2/2.
**Manager's gate** at `f65c4ba8`, Release: build 0 warnings, 0 errors; Unit 1411/1411 (41 m 6 s — the Mac was in clamshell sleep from 12:42 CEST during the run); Fault 439/439
(1 m 49 s); Integration 721/722, 1 skipped (11 m 16 s) → 0 failed. Names vs `main`: 2203 → 2207, 0 removed, 4 added. Scan clean; no trailers.
**CI:** branch run 37444607427 at `0c260181`: ubuntu ✓ 13 m, macOS ✓ 15 m, windows ✓ 51 m, package ✓. Tests box: NOT RUN. The builder's check at 12:09 CEST: "the machine
does not answer". The manager's check at landing: "NO - his own OFT.Platform is open; nothing of ours runs beside it" (respected, not retried).
**NOT done, NOT verified:** ATAS (nothing claimed or run there). Integration ran on CI and in the manager's gate only. No test covers an absent close over a book someone else
already flattened (flat → confirmed, nothing sent). The app was not run.

## 2026-10-06 — U-close-once landed: one close of a position at a time — a close sized from the position is refused while an earlier same-side market order on that account and instrument has no final answer — and a paper run is ended once

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-close-once.md` (written by seat A's probe leg, which RAN two ENDs at once at
`2952c285` into "orders at the wire: 2" and a short; queued `c6a283e0`; dispatched `32d654b7` beside -b on the orchestrator's ruling); the builder rebased
over -b's landing onto `22df95a7`; rebased by the manager onto ``8d293dae` (with U-fix-bridge-heartbeat and U-flatten-absence)`, src+tests patch-id identical. Merge `e52fa8de` (ff-only), 4 commits (3 items + the
report), 6 files, +436/−21. No schema change. MONEY PATH: the gateway's close guard and the deployment END; the press path and `ForwardRuns.cs` untouched;
rule 3 kept — the new refusal comes BEFORE any order row, so `refused` stays proof nothing left; nothing in flight is closed over, re-sent or cancelled.

- **Item 1 (`4ff2b5cf`):** `ClosesInFlightOn` beside `UnresolvedReducersOn`, read by both `RefuseAnUnresolvedReducerOrThrow` overloads (`CloseAsync`'s early
  check, and inside `_dispatchGate`, the approval path too): a `PLACE` MARKET order on the same connector, account and instrument, same side, `DISPATCHING`,
  `ACKNOWLEDGED`, `WORKING`, `PARTIALLY_FILLED`, `CANCEL_PENDING` or `RECONCILING` ⇒ `CLOSE_IN_FLIGHT` naming it and its state; UNKNOWN keeps its rule and code.
  MARKET only, so the run's stop and target still rest side by side.
- **Item 2 (`92683729`):** a paper run is ended once — one gate per run (`EndGateOf`): `EndPaperDeploymentAsync` takes it after its ended check and re-reads the
  row inside, so a second END writes no operation, cancels nothing and answers the run as the first left it; -a's owed close only TRIES it.
- **Item 3 (`1c9afa0e`):** `CONTRACTS.md` — the agent's-close rule gains the in-flight arm; "two ENDs at once" becomes a claim, the rest stays NOT claimed.
- **Declared deviations, accepted:** (1) the predicate skips the caller's OWN request id, so a repeated `close` gets the normal replay; (2) a waiting END
  writes `deployment_end_waits` to the engineering log; (3) the owed close tries the gate rather than waiting on it; (4) test (a) holds 2 and adds a market sell
  (−3 on the base); (5) `close-all`'s schema sentence (`GatewaySchema.cs:424`) names the new code.
- **Judged at landing — a `WORKING` market row the platform's stream never moves holds that position's AUTOMATED closes** (END, owed close, runner
  exits, the agent's close), refused `CLOSE_IN_FLIGHT` each minute with the blocker named on the run's line. Read on the shipped connectors: the Simulator
  fills a market order at once (`FakeBroker` `Fill = FillImmediately`; only the developer host `GatewayHost/Program.cs:124-125` can leave one working), and
  paper reports a cancel inside the call (`PaperConnector.cs:443`), so the END's own cancel cannot block its close there; on ATAS a cancel or fill arrives by
  the stream and the owed close goes out on the next pass — a LOST update (a bridge drop) is the residual. The owner's Close all positions and the loss
  flatten are outside this guard and still close. OWED before any live use: in-flight MARKET rows re-read against the platform's order list so a stale
  row settles (a unit to brief); and seat P's press-path settle-before-send for a same-side close in flight.

**Verified by running (the builder, quoted; then the manager's gate):** builder at `e225a9fc`: Release `--no-incremental` 0 warnings, 0 errors; Unit 1410,
Fault 440, 0 failed; `AgentCloseOverAnUnknownCloseTests`, `PressSettlesAnUnknownCloseTests`, `PaperDeploymentTests` 3× 23/23. RED before: (a) "Expected: 1
Actual: 3" — the second close "ok", a market sell "ok", "ES -3"; (b) rests — 2 flattens, "orders at the wire : 2", "-1 after"; fills — B refused
`POSITION_MOVED`, the run owing over 0. Mutants watched red: (i) the in-flight arm dropped ⇒ (a) "Expected: 1 Actual: 3"; (ii) the END's gate a fresh
semaphore per call ⇒ (b) "Assert.Single() Failure: The collection contained 2 items" in both arms.
Manager's gate at `e52fa8de`, Release: build `--no-incremental` 0 warnings, 0 errors; Unit 1411/1411 (8 m 27 s, the slow Mac), Fault 443/443 (1 m 49 s), Integration 721/722, 1 skipped (11 m 15 s) → 0 failed — on the tree with U-fix-bridge-heartbeat and U-flatten-absence, which the branch run predates.
Names vs `main` (git objects): 2207 → 2210, 0 removed, 3 added ([Fact]/[Theory] 2161 → 2164). Scan: one judged false positive, a cancellation token named `looking` in test (b), excluded by name; no trailers; `rev-list --count` 0 both ways.
**CI:** branch run 37445685733 at `e225a9fc` (on `22df95a7`): success on ubuntu-latest (13 min), macos-latest (16 min), windows-latest (60 min), package.
U-runner-exit-hygiene-b's landing push `e01198d6`: run 37443797669 RED on windows-latest only — `CoidWitnessTests.A_refused_rename_is_attempted_exactly_five_times_and_then_gives_up` (both cases) "the retry took 2335 ms — the budget is not bounded", a wall-clock bound in a run whose Windows suites took 25–47 min; -b touched no witness code, and the same code is green on Windows at `8029b53c` and `003c0a76` (runs 37443830809, 37443899668) — a hosted-runner red, recorded red, the fixer seat P's (FLEET). **Tests box:** NOT RUN — `ready` at 16:04 CEST: "NO - his own OFT.Platform is open; nothing of ours runs beside it" (its owner's ATAS was open; nothing of ours started).

**NOT done, NOT verified:** the press path still sizes a close beside a resting one (seat P, owed); a runner pass begun before an END; a fill the position
read lags (ATAS, the box); the stale-row reading above was read, not run; no box run, no app run.

## 2026-10-06 — U-fix-press-budget landed: the owner's Cancel all and Close all presses spent their two seconds on their own store writes and could refuse themselves before the wire; both are now charged for platform time only

One fresh Opus fixer under seat P built it from `docs/briefs/U-fix-press-budget.md`, briefed by seat P on the first sighting: windows-latest, run 37391256380, branch
`r-containment` with no src/tests change, `PressIdShapeTests.The_operator_cancel_all_names_its_legs_without_the_brokers_order_id`, "Expected: 2 / Actual: 0", 46 s.
Merge `d14b4aeb` (ff-only), 6 commits; rebased four times by the fixer, the last over seat A's `U-close-once`. CONTRACTS conflicted there: both units had appended a
closing section, and the fixer kept both, with seat A's CLOSE_IN_FLIGHT passages untouched. No rung. MONEY PATH: the owner's emergency presses.

- **Item 1 — the cause, measured on windows.** A branch-only probe stamped every platform call and SQL statement with the budget left. Runs 37413619642, 37413622132,
  37413624368: 270 presses of each kind, all green, with at least 1781 ms left at the read. The natural red did NOT recur.
  Before reading orders, the cancel-all makes no platform call, only three commits: the press row, the pause's `health_event` and DISPATCHING. The budget moved at those
  commits and nowhere else (26+18+18 ms → 1937 ms left; wall time minus commit time −2..26 ms per press). Seen naturally on a smaller scale: one close-all's nine commits
  took 2688 of its 2689 ms, and its next call was refused at −31 ms; one bare commit took 5207 ms.
  CONTROL (the deadline gone at the first platform call, or a second writer holding the store across the press row) → `Expected: 2 / Actual: 0` byte for byte, 78 of 78
  on windows. The diagnostic was removed in `c699572c`; src, tests and `.github` net to main's.
- **Item 2 (`9f358922`):** both presses open `RiskReducingScope.BeginExcludingTheStore` (`TradingGateway.cs:8147`, `:8422`). Their two seconds now bound the platform,
  and the press's own store writes cannot spend them. **Owner's view, applied by the seat:** the earlier doc's "the owner's press keeps `Begin`" is superseded, because a press
  that refuses itself on a slow disk fails the emergency it exists for. The doc, `StoreTime`, CONTRACTS, USER-GUIDE and MONITORING-PHASE say so.
  The AGENT's risk-reducing pipe scope (`GatewayPipeServer.cs:1211`) is LEFT on `Begin`, with the reason written there: the shutdown drain is derived from that budget
  bounding the whole handler. Accepted.
- **Declared deviation, ACCEPTED by the manager:** in `OperatorPressIsAnEmergencyTests.The_position_read_before_the_close_inherits_the_scope`, `Assert.Single(deadlines.Distinct())`
  cannot hold once store time is refunded: each read's deadline moves by design (three distinct values measured with the fix, one value three times without). The test now
  asserts the same three facts against a new `RiskReducingScope.OpenedDeadlineAt`: one opened deadline, every read bounded, never earlier. Name kept. The edits to
  `AgentCloseAtDispatchTests` and `CompositeOwnerTests` are comment-only (checked by the manager).

**Verified by running (the fixer, quoted; re-run by it on the rebased tree).** RED before, store held 3001 ms against 2000:
- cancel-all `Expected: 2 / Actual: 0`, book `[FB-1 ES WORKING; FB-2 NQ WORKING]`;
- close-all `Expected: FILLED / Actual: UNKNOWN`, `[ES 2]` left open.
Mutant, the cancel-all back on `Begin` → its test `Expected: 2 / Actual: 0`, legs `0 []`; the close-all's test stays green; restored byte-identical.
Fixer's gate at `089143cf`: Release 0 warnings; Unit 1411/1411; Fault 445/445; seven touched classes 3×, 20/20 each.
**Manager's gate** at `d14b4aeb`, Release: build 0 warnings, 0 errors; Unit 1411/1411 (8 m 27 s); Fault 445/445 (1 m 54 s); Integration 721/722, 1 skipped (11 m 13 s)
→ 0 failed. Names vs `main`: 2210 → 2212, 0 removed, 2 added (one slow-store test per press). Scan clean; no trailers.
**CI:** branch run 37480128757 at `d090aabf` (code `089143cf` plus the report; `d14b4aeb` changes only the brief): ubuntu ✓ 13 m, macOS ✓ 15 m, windows ✓ 49 m, package ✓,
Timing first try everywhere. Before the last rebase: 37444558954 and 37422916706, all four jobs success. Tests box: NOT RUN — "his own OFT.Platform is open" (the fixer and the manager, once each).
**Also settled, separately:** the macOS sighting `SweepRequestIdTests.A_sweep_pays_the_emergency_budget_once_not_once_per_rpc` (NullReferenceException at `:333`, run 37420396771)
concerns the AGENT's pipe sweep. A throwaway control (2100 ms latency) reproduced it: a book read clipped by the deadline answers `ok=False` with no Data, and the test casts
`Data` without asserting `Ok` — a test fault, owed as a light fix. What spent ~100 ms on that runner: NOT VERIFIED.
**NOT done, NOT verified:** a natural recurrence of the stall (the cause stands on per-step measurement and exact controls); any Windows box run; the app not run.

## 2026-10-06 — U-dataset-version-once landed: a re-collection no longer deletes the raw files earlier datasets rest on, a dataset version is allocated where it is written and a dataset file is never replaced, and a file unreadable for a moment is no longer rejected for good

One fresh Opus builder under seat P built it from `docs/briefs/U-dataset-version-once.md`, written by seat P's survey of a windows-latest first sighting: main `c6a283e0`,
run 37421444199, `DataLicenceTests.A_collection_takes_its_reading_only_from_the_built_in_origin` at `:168`, "v2"/"v1". The survey MEASURED that the evidence path was reachable.
Merge `b78c6b41` (ff-only), 5 commits, rebased over U-fix-press-budget with an identical src+tests patch-id; 14 files, +739/−162; no rung.
EVIDENCE PROTECTION (`CLAUDE.md`), held to the money-path proof burden.

- **Item 1 (`219f1518`):** `TestEnv.NewPair` beside `NewDb`; every test that collects through `MarketDataService` gets a pair of its own. Before, two test classes shared one
  home's `binance/BTCUSDT/1m/` folder, and one of them corrupted raw bytes there on purpose: the sighting's mechanism, reproduced every time by (iii).
- **Item 2 (`0d0fcaa5`):** raw evidence is write-once. A fetch downloads into `raw/.staging/<guid>/`, now the only place a fetch deletes. The bytes are kept at the vendor's
  name if that is free or already holds them; otherwise at `name.<sha16>.ext` by the same rule. **Declared deviation, accepted:** a new `MonthOutcome.NameTaken` for "both
  names hold other bytes", refused in words.
- **Item 3 (`3d96d9d6`):** `DatasetStore.RecordNew` does it all in one `db.Write`:
  - takes `v{max+1}` over the ledger's labels read as numbers;
  - skips any `v{n}.csv` that already exists (a crash orphan: never replaced or deleted);
  - moves the staged file with `overwrite: false`, then inserts.
  `Record` refuses, in words, a (pair, interval, version) the ledger holds, and `NextVersion` is gone. Fixture labels were made distinct and are named in the report
  (`DataLicenceTests`, `VenueCatalogTests`, `DataOverPipeTests`, `PaperAllocationGateTests`, `PaperDeploymentTests`, `CostModelPinTests`). Each is a label, not an assert.
- **Item 4 (`deb1f450`):** `FirstMismatch` → `Mismatch(Reason, Proven)`. A missing file and other bytes are persisted as REJECTED, as before. Any other IOException or
  unauthorised access is REJECTED for THAT read only, saying "…could not be read just now (…); nothing was recorded, and it is checked again on the next read", with no
  `Reject`. **Declared deviations, accepted:** the read goes past an unreadable file, so a proven mismatch elsewhere is still recorded; `DatasetRecord.RejectedThisReadOnly`
  (init-only, never stored) lets `Rebuild` drop "Collect the months again".

**Verified by running (the builder, quoted).** RED before, the new tests on the base product `d2943a82`:
- (i) "the raw archive file for 2025-09 is no longer on disk at …";
- (ii) "… no longer matches the hash recorded for it (c1089074… became 325a7b13…)";
- (iii) "ledger B's collection wrote ledger A's file …";
- (iv) an unrecorded `v2.csv` overwritten;
- (v) "the normalised dataset file is no longer on disk …" while it was only held;
- the label refusal: "No exception was thrown"; the both-names refusal: values equal.
The guard `A_raw_file_that_is_gone_is_still_rejected_for_good` was green before and after. Mutant: staging dropped, so the fetch deletes and downloads at the vendor's name
as before → (i) red; restored byte-identical.
Builder's gate: Release 0 warnings; Unit 1419/1419; Fault 443/443; the touched classes 3×, all green.
**Manager's gate** at `b78c6b41`, Release: build 0 warnings, 0 errors; Unit 1419/1419 (9 m 7 s); Fault 445/445 (1 m 54 s); Integration 721/722, 1 skipped (11 m 15 s)
→ 0 failed. Names vs `main`: 2212 → 2220, 0 removed, 8 added. Scan clean; no trailers.
**CI:** branch run 37481826449 at `deb1f450`: macOS ✓ 15 m, windows ✓ 53 m, ubuntu ✓ 13 m, package ✓. The run before it, 37480244910 at `3048df00`, was red on all three
in `PaperFrictionTests…_is_refused` (two "BTCUSDT 1m v1" rows from `CostModelPinTests.Dataset`); the duplicate refusal caught it, and item 3 fixed it.
Windows ran (g) green on both runs, which shows nothing either way about an intermittent red. Tests box: NOT RUN — "his own OFT.Platform is open" (the builder and the manager, once each).
**NOT done, NOT verified:** `SettingsView`, the data-bars refusal and `MissionSituation.DataLine` still add re-collect advice to a REJECTED that holds for this read only
(owed, light). A staging folder left by a crash is not swept. An interrupted download is no longer resumed by the next press. No app run, no box run.

## 2026-10-06 — U-inflight-settle landed: a market order whose platform update was lost is read back from the platform's own order list and settles only from a final answer the platform gives, so it stops holding its position's automated closes

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-inflight-settle.md` (written by seat A's read-only survey leg at `bdf5affa`,
which ran nothing; queued `2fcb64df`; dispatched `2a82133b`); the builder rebased onto `6afe98cb`, where `land.sh prep` found it ("Already on main"), so the
item shas below are the ones on `main`. Merge `47bee8f7` (ff-only), 9 commits (red-first tests, 4 items, 3 follow-ups, the report), 6 files, +627/−27. **No schema
change (30).** MONEY PATH: the gateway's close guard and the order-state writer. Rule 3 kept — only a FINAL answer the platform gives settles a row; a
throw, a live answer or an absence that proves nothing settles nothing, and the settle sends, re-sends, cancels, flags and pauses nothing. Rule 2 kept —
"not listed" decides only where `AbsenceDecidesALostClose` holds (paper, the Simulator; never ATAS), past the clock the loss confirm already uses. Accounting:
the final state goes through the stream's own writer, and a fill enters only by `RecordFill` (the settle marks the fill pull due).

- **Item 1 (`6d6b09fa`):** `OnOrderChanged`'s write becomes `ApplyAPlatformAnswer` (owner-state skip, `CanTransition`, `Transition`, `StateChanged`, terminal
  `Wake`), the one writer the stream calls; behaviour unchanged. **Tests (`d362b5ad`):** red-first (a)–(d).
- **Item 2 (`e20e2927`, wording `47c514dd`):** `Task<InFlightAnswer> SettleAnOrderInFlightAsync(ExecutionRequest row, CancellationToken ct)` and
  `record InFlightAnswer(bool Settled, OrderInfo? Live, string Why)` beside `ClosesInFlightOn` (private, as it is): one unflagged, non-press PLACE row in a
  stream state, only where `ReconciliationProvable`; `Why` names the owner of a row it does not take. `AskTheHistoryAsync` now returns
  `(Verdict, Live, Undecided)`; a settle's last error names the read it came from.
- **Item 3 (`b5c504e2`, `6660409c`):** `SettleStaleOrdersInFlightAsync` on the health pass right after `ConfirmLostClosesAsync`: stale = `Now −
  AbsenceCountsFrom ≥ AbsenceGrace`, no new number; `inflight_settled`, and `inflight_undecided` once per change; it never throws but on cancel.
- **Item 4 (`cd547ceb`, `43def342`):** `CONTRACTS.md`: the NOT-claimed `WORKING` row becomes a claim with its limits; the `CLOSE_IN_FLIGHT` refusal and
  `GatewaySchema`'s `close` add "or once TradeAgent reads its outcome from the platform".
- **Judged at landing.** (1) **ATAS residual, no in-app way out:** an order ATAS does not list after a lost update stays `WORKING` and unflagged, so it is
  not on the owner's card (the card lists `Unreconciled()`, `DashboardView.cs:198-203, 306`), and its position's automated closes stay refused. The gap
  existed before this unit; the unit narrows it to this case. OWED BEFORE ANY LIVE USE: an in-flight row the platform cannot account for, past the clock,
  reaches the owner's card with its evidence, and his press settles it (seat A briefs it). (2) **Press rows:** nothing settles a stale `WORKING` press row,
  but it is flagged from its write-ahead, pauses trading and waits for the owner's `ForceResolve` — for seat P's `U-press-close-once`, which calls item 2's
  function (the orchestrator's ruling, 22:03). (3) Seat P's queued `U-valuation-close-confirm` takes `AskTheHistoryAsync`'s 3-tuple.

**Verified by running (the builder, quoted; then the manager's gate):** builder: Release `--no-incremental` `0 Warning(s)`, `0 Error(s)`; Unit `Failed: 0,
Passed: 1419`; Fault `Failed: 0, Passed: 449`; `InFlightSettleTests` 3× (3) and `PaperDeploymentTests` 3× (14), 0 failed. RED on the base: (a) `Expected:
FILLED Actual: WORKING`, the next close `CLOSE_IN_FLIGHT`; (b) `Expected: CANCELLED Actual: WORKING`, the owed close refused on two minutes; (d) past the
clock `Expected: CANCELLED Actual: WORKING`; (c) green, a guard. Mutant: `AbsenceDecidesALostClose` dropped from `AskTheHistoryAsync` ⇒ (d) red,
`Expected: WORKING Actual: CANCELLED`; reverted.
Manager's gate at `47bee8f7`, Release: build `--no-incremental` `0 Warning(s)`, `0 Error(s)`; Unit 1419/1419 (37 s), Fault 449/449 (2 m 3 s), Integration 721/722, 1 skipped (11 m 13 s) → 0 failed.
Names vs `main`: 2220 → 2224, 0 removed, 4 added. Scan: `SCAN CLEAN`; no trailers; `rev-list --count` 0 both ways.
**CI:** branch run 37527572700 at `47c514dd`: success on macos-latest (14 min), ubuntu-latest (12 min), windows-latest (51 min), package (5 min); only docs
after it (`43def342`, `47bee8f7`). **Tests box:** NOT RUN — `ready : NO - the machine does not answer (…)` at 22:27, 22:41 and 23:35 CEST.

**NOT done, NOT verified:** whether ATAS lists a filled market order under its client id after a bridge drop (code read, NOT VERIFIED: the box not granted
and unreachable); the ATAS residual above; a run's own flatten settled `CANCELLED` owes no close (`OwesItsClose`) — out of scope; a lagging position read
stays NOT claimed; no box run, no app run.

## 2026-10-07 — U-tape-read landed: every role can read the tape, bounded and read-only through one withholding place, and the owner can see it is recording

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-tape-read.md` (READY since 2026-10-02; re-checked against `9cd0f5e5`, pointer
drift amended `7f872732`; dispatched `bdf5affa`). Rebased by the manager onto `1828a188` (over U-inflight-settle), src+tests patch-id identical
(`d6e31e9329a8`). Merge `9ef81d30` (ff-only), 7 commits (4 items, 2 follow-ups, the report), 22 files, +2417/−31. **No schema change** (main 30, tape.db 1).
**M0's pin:** the orchestrator pinned M0's GO sha at `1828a188` before this merge (02:26) (Law 10: the loop observed before its inputs widen).
Nothing can write the tape through it: `TapeReader` opens `tape.db` read-only with `query_only` and the gateway holds no `TapeStore`; the ONE place that
withholds a quarantined payload is `TapeStore.Served`, used by `AsOf` and the range read alike.

- **Item 1 (`2c64c819`):** op `data-tape` for every role and a caller with none; drain-table row at 0; limit 1–5,000 (default 1,000; more refused in words),
  4 MiB of rows, newest arrival first; source/series/subject validated in words, `--subject` optional; the caller's audience handed to the reader.
- **Item 2 (`933ce8d8`):** `trade data tape …` with a `source credit:` line; `GatewaySchema` entry; three `WorkspaceBuilder` lines; the harness `data` tool.
- **Item 3 (`b1d59af1`, `7024d248`):** `data-list.tape` (switch, rows, first/last arrival, last error, symbols or subject key, GDELT's citation);
  `status.tape` {recording, rows_today, failures_last_hour, market_context, gdelt_news with daily_cap_reached_today, sources}; the daily report's
  "market context tape" line (rows, gaps, errors, GDELT credit).
- **Item 4 (`c321e608`, `f219341b`):** `CONTRACTS.md` (the op, its bounds, the reader that cannot write, NOT CLAIMED 12–13), `USER-GUIDE.md` ("Seeing that it is
  recording"); the quarantine test's announcements on a reserved `.invalid` host.
- **Declared deviations, accepted:** (1) argument `before`, a row-id cursor — a GDELT file's rows share label AND arrival, so no time continues a capped answer
  exactly; (2) the byte cap counts the reply's rows as written; (3) `GdeltGkg.CapNotePrefix` replaces the recorder's literal, so status reads the cap off the
  tape; (4) `DataOpsTests.There_is_no_operation_on_this_channel_that_writes_a_dataset` now names the three `data-` ops (stronger; not renamed).

**Verified by running (the builder, quoted; then the manager's gate):** builder at `70ca3f8c`: Release `--no-incremental` `0 Warning(s)`, `0 Error(s)`; Unit
`Failed: 0, Passed: 1424`; Fault `Failed: 0, Passed: 445`; `TapeReadTests`+`DataOpsTests` 3× 12/12, `TapeOverPipeTests` 3× 8/8. RED before: (a) with the
dispatch arm removed → `unknown operation 'data-tape'`. Mutants: (i) the reader's limit check removed ⇒ (a) `Expected: 5000 Actual: 5001`; (ii)
`TapeStore.Served` returning the payload ⇒ (g) `Expected: Null Actual: String`.
Manager's gate at `9ef81d30`, Release: build `--no-incremental` `0 Warning(s)`, `0 Error(s)`; Unit 1424/1424 (9 m 7 s), Fault 449/449 (1 m 56 s), Integration 729/730, 1 skipped (11 m 16 s) → 0 failed.
Names vs `main`: 2224 → 2237, 0 removed, 13 added. Scan: two judged false positives in `TapeOverPipeTests.cs`, excluded by name — "machine token on every frame" (a doc comment's prose about the pipe's
machine token) and `"attempt-tape-" + role).Token` (the token of a grant the test rig issues itself); no trailers; `rev-list --count` 0 both ways.
**CI:** branch run 37529866030 at `70ca3f8c` (on `bdf5affa`): success on ubuntu-latest, macos-latest, windows-latest (66 min), package — each Unit 1423/1423,
Fault 440/440, Integration 638 passed; macOS's Timing category red once on its first attempt in `SweepRequestIdTests.A_sweep_pays_the_emergency_budget_once_
not_once_per_rpc` (NullReferenceException at :333), green on the category's second attempt — a path this diff does not reach; a sighting for seat P.
**Tests box:** NOT RUN — `ready : NO - the machine does not answer (…)` at 22:35 CEST and at this landing.

**NOT done, NOT verified:** no Settings-card line (status and report only, per the brief); the status and report counts stand on `TapeReader.ArrivalSlack` (a
clock stepped back > 10 min is not covered); `data-tape`'s cost on a large tape not measured — no arrival index until a tape rung adds one; the read-only
WAL open on Windows verified by CI only; no box run, no app run.

## 2026-10-07 — U-loss-hold-release landed: a lost loss-budget close the owner has answered on the Dashboard is a decided leg of the confirm, so a closure no longer stays held for ever over a leg he has settled; the platform's history still outranks his word when it holds the close live

One fresh Opus builder under seat P built it from `docs/briefs/U-loss-hold-release.md`, written by seat P from its read-only survey at `bdf5affa` (every fact reproduced
by a probe: on ATAS-like history the closure stayed held after the owner answered; the Dashboard asked him to confirm records when none was left). A fresh merge leg
rebased it onto seat A's `U-inflight-settle` (`1828a188`): one conflict at the confirm's call site, A's 3-tuple read as A reads it. Seat P's landing prep rebased it
onto `e2f4daf8` (seat A's U-tape-read) and `a50ef414` (docs only), src+tests patch-id identical both times (`79c48a3e1111`).
Merge `2939b9c4` (ff-only), 5 commits (items `809a074f`, `1a26adcc`, the arm `13256eb1`, two report commits); no rung (kv JSON only). MONEY PATH: the loss boundary's confirm and closing again. Extends `U-flatten-confirm` (its deviation (c)).

- **Item 1 (`809a074f`):** `LostCloses` keeps a lost close the owner settled (`SettledByTheOwner`: terminal, his prefix, not flagged or latched); `TheOwnersAnswerAsync`
  makes it a verdict (his state and fill, "you confirmed it on the Dashboard: <note>"); where `ReconciliationProvable` the platform's order list is still read, and a close
  it holds non-final decides nothing, named ("… that outranks your answer"), as does a read that throws. `ReconciliationProvable` now gates only the history question, so an
  ATAS closure is confirmed once every lost close is answered; then the landed confirm (flat → nothing sent) and again-flatten (cancel-first at the platform), unchanged.
- **Item 2 (`1a26adcc`):** `ConfirmSentence`, the again's sentence and `HeldBy` name the verdict's source (the history, YOUR ANSWER ON THE DASHBOARD, or both); a not-flat
  outcome asks him to confirm records only while one is flagged or latched. CONTRACTS, USER-GUIDE, AGENTS.md (`WorkspaceBuilder`) and the status schema say so.
- **Decision (seat P, extending the orchestrator's `U-flatten-confirm` ruling; told to it, not overruled):** an owner-settled lost leg is a decided leg; an open book is
  then closed again through the again-flatten. **Declared deviations, ACCEPTED:** (a) his answer counts only past `AbsenceCountsFrom` + `AbsenceGrace` (a close in transit is
  not there for him to see; tested one second short: nothing written or sent); (b) a terminal history state other than his is named beside his words — the close-again is
  decided by the fresh read of the book, not by the verdict; (c) `ApplyTheConfirm` unflags nothing while an answered row is flagged again (test (f); that line dropped →
  (f) red); (d) (c) gained a "history read throws" arm. The merge leg's declared choice, ACCEPTED: the veto keeps its own order-list read rather than A's `Live`, so a fills
  read that throws cannot block his answer. Nothing of A's changed (sha256 per function, the merge leg).

**Verified by running (the builder, then the merge leg, quoted).** RED before on the base product: (a) `Expected: 1 Actual: 0` confirms, the Dashboard "AI trading is
paused until you confirm those records" with every record answered; (b) both arms `Expected: 1 Actual: 0`, "closes on the wire : 1, position ES 1"; (e) `Expected: 1
Actual: 0`, "closes 1, cancels 0, ES 1"; (c) its naming red. Mutant — the live-order veto dropped — → (c) "Expected: 0 Actual: 1" confirms, "closes on the wire : 2",
watched by the builder at `79e432c8` and RE-WATCHED by the merge leg on the rebased tree (`97248404`); restored, sha256 OK.
Builder's gate at `79e432c8`: Release 0 warnings; Unit 1419/1419; Fault 453/453; classes 3×. Merge leg's gate at `e095960d`: Release 0/0; Unit 1419; Fault 457;
`LossHoldReleaseTests` 8/8, `LossFlattenConfirmTests` 12/12, `InFlightSettleTests` 3/3, each 3×.
**Manager's gate** at `469bc7f6`, carried to `2939b9c4` (only docs moved), Release: build `--no-incremental` 0 Warning(s), 0 Error(s); Unit 1424/1424
(9 m 9 s); Fault 457/457 (1 m 56 s); Integration 729/730, 1 skipped (11 m 16 s) → 0 failed. Names vs `main`: 2237 → 2243, 0 removed, 6 added ([Fact]/[Theory] 2191 → 2197). Scan clean; no trailers.
**CI:** branch run 37537618216 at `e095960d` (the rebased code; the tip adds the report only): ubuntu ✓ 12 m, macOS ✓ 17 m, windows ✓ 52 m, package ✓. Before the rebase:
37529792550 at `79e432c8`, all four success. Tests box: NOT RUN — "the machine does not answer" (22:43, 23:47, 23:59 CEST). At landing (02:58 CEST) the box was
reachable: "NO - his own OFT.Platform is open; nothing of ours runs beside it" → NOT RUN, respected. Landing CI on `main`: a waiter is armed.
**NOT done, NOT verified:** OWED — the closing again's own lost close, once he answers it, still holds the closure (one confirm per breach; RUN by probe) → folded into
`U-valuation-close-confirm` as one class fix (every lost app close confirmed per close generation). ATAS not run (no box); the app not run.

## 2026-10-07 — U-bridge-liveness-clock landed: the ATAS connector measures the bridge's liveness and its auth grace on the monotonic counter its own deadlines read, so a wall clock stepped backwards no longer keeps a silent bridge READY

One fresh Opus builder under seat P built it from `docs/briefs/U-bridge-liveness-clock.md` (queued `a562283a` from `U-fix-bridge-heartbeat`'s finding; pointers re-checked by
seat P at `9cd0f5e5`, `7aba58b0`). Owed BEFORE ANY LIVE USE. Merge `c25236c5` (ff-only), 10 commits (item 1 `defd8c57`, item 2 `7ab252d5`, eight report commits); rebased by seat P's prep
onto `e4f38682` with an identical src+tests patch-id. No rung. MONEY PATH: the ATAS connector's liveness truth (a dead bridge must not read alive; `CLAUDE.md` rule 3).

- **Item 1:** `AtasConnector.LivenessClock`, a `TimeProvider` whose monotonic half is `Environment.TickCount64` — the counter the write, answer and emergency deadlines already
  read. The hello, heartbeat and arrival stamps (now `long`, under `Volatile`), the quiet-peer drop (`PeerHasGoneQuiet`), the health verdict (`GetHealthAsync`) and both auth-grace
  readings read only that half, through one `Since()`. Timeout, poll and comparisons unchanged. Left on the wall clock as INSTANTS, not durations: `BridgeAuthFailure.When`
  (`BridgeServer.cs:225`, shown to the owner) and a history read's `since` (the broker's timeline). `_lastHeartbeat` was never shown, so no displayed time was dropped.
- **Item 2:** `BridgeLivenessClockTests` (Integration: a real pipe, a stub bridge, a test clock whose wall half steps; no sleeps): (a) a bridge silent while the wall steps
  BACK 1 h → READY at exactly the timeout, DEGRADED 1 ms past it, then dropped; (b) a live bridge pulsing while the wall steps FORWARD 1 h → stays READY, a read answered;
  (c) the auth grace under the backward step → "connecting" at exactly the grace, "silent" 1 ms past it.
- **Declared deviation, ACCEPTED:** RED-before is measured through item 1's seam (the base's seven wall reads put on the seam's wall half), because the base has no clock a test
  can step; the seam carries a wall half the product never reads, for the tests. **Not done, judged:** `AtasHealth.cs:299`'s detection-cache TTL stays on the wall clock
  (outside both files, not liveness) — owed light.

**Verified by running (the builder, quoted).** RED before: (a) "Expected: Tuple (READY, DEGRADED, True) Actual: Tuple (READY, READY, False)"; (b) "Expected: READY Actual:
DEGRADED"; (c) "Expected: Tuple ("connecting", "silent") Actual: Tuple ("connecting", "connecting")". Mutant `GetHealthAsync` back on `DateTimeOffset.UtcNow` → (a) red
"Actual: Tuple (READY, READY, True)"; restored. Builder's gate at `c51502ba`: Release 0 warnings; Unit 1419; Fault 445; Integration 724 + 1 skipped; 0 failed;
`BridgeLivenessClockTests` 3/3 ×3, `BridgeRoundTripTests` 41/41 ×3 (its `Timing` member included). `ConnectorSendDeadlineTests` 20× locally: 1,020 executions, 0 failed.
**Manager's gate** at `c25236c5`, Release: build `--no-incremental` 0 Warning(s), 0 Error(s); Unit 1424/1424 (9 m 14 s); Fault 457/457 (1 m 55 s);
Integration 732/733, 1 skipped (11 m 15 s) → 0 failed. Names vs `main`: 2243 → 2247, 0 removed, 4 added (three tests and the `StepWall` helper; [Fact]/[Theory] 2197 → 2200). Scan clean; no trailers.
**CI:** branch run 37532362047 at `c51502ba` (the code tip; later commits docs only): ubuntu ✓ 15 m, macOS ✓ 16 m, windows ✓ 61 m, package ✓. The first run, 37524459410 on
the same tip, went red on windows in `Timing` only, both judged by seat P: (1) `OperatorPressIsAnEmergencyTests.A_wait_the_simulator_predicted…` "2198 ms" — the same red on
`main` WITHOUT this diff (37521226865, 37521235649, 37521297827); seat P's survey: the simulator's predicting wait, a test-rig cause → `U-test-hygiene-2` item 6; (2) on the retry,
`ConnectorSendDeadlineTests.A_cancellation_fails_fast_on_a_stalled_bridge_whoever_issued_it("button")` "11.80s behind a stalled write" — NOT reachable from this diff on the
builder's evidence (no write, answer or cancel deadline reads a different clock; the stalled peer never beats); a FIRST SIGHTING on the emergency path → a read-only survey owed.
Tests box: NOT RUN — "the machine does not answer" (22:12, 23:14), "his own OFT.Platform is open" (02:35), and at landing (11:20 CEST) "the machine does not answer". Landing CI on `main`: a waiter is armed. Seat P held `land` across
the weekly stop (03:46 → 11:20); the gate had finished at 04:09 and nothing moved on `main` meanwhile.
**NOT done, NOT verified:** why either windows `Timing` red happens on windows-latest (no Windows hardware); no ATAS run, no app run; no doc changed.

## 2026-10-07 — U-inflight-owner landed: an order the platform answered and no longer lists reaches the owner's card past the clock, pausing trading like any unconfirmed order, and the reconciler never writes off an order carrying the platform's reference as never sent where absence proves nothing

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-inflight-owner.md` (written by seat A's read-only survey leg at `1828a188`,
which ran nothing; queued `b6699c8a`; dispatched `91f74ba2`); the builder rebased over seat P's `U-loss-hold-release` onto `e4f38682`; rebased by the manager
onto `3db0c533` (over `U-bridge-liveness-clock`) then `ba4da5b2` (docs only; the gate carries), src+tests patch-id identical (`9e06c24241fc`). Merge `1fa820a6` (ff-only), 6 commits (5 items, the report),
9 files, +716/−40. **No schema change (30).** MONEY PATH: the in-flight sweep, the reconciler's absence rule, the owner's card. Rule 3 kept — such an order
may have filled, so it is recorded UNKNOWN and reconciled by the reconciler's own writes (CAS'd on the state read), never settled from that absence nor
written off "never reached the broker"; a throw, a live answer or an unprovable connector hands nothing over. No terminal: the way out is the existing
unconfirmed card. Operator authority: the one press is `ForceResolve`, reached only from the card, two-press with a required note; no pipe op, no verb.

- **Item 1 (`5c40b5ed`):** `AskTheHistoryAsync` hands back `Unlisted` as a named field — true only where both reads answered, neither lists the order and
  absence decides nothing; `InFlightAnswer.Unlisted` carries it (seat P's `U-press-close-once` uses the same field). No behaviour change.
- **Item 2 (`fe07fdce`), a defect on `main` closed:** `AnsweredWhereAbsenceProvesNothing` — the reconciler's absence arm left alone a row carrying the
  platform's reference where absence proves nothing; before it, an ATAS order with a reference (and `RecordIndefinite`'s) was written off `CANCELLED`
  "never reached the broker", unflagged, and trading resumed. `Adopt` and fills under its id still settle it.
- **Item 3 (`fc17104a`):** `HandOverToTheReconciler`, from the sweep only: CAS `→ UNKNOWN`, flagged, the evidence as its last error, then `→ RECONCILING`; a lost
  first CAS hands nothing over; `inflight_handed_over`, an activity line. `SettleAnOrderInFlightAsync` still flags nothing.
- **Item 4 (`d7b721fd`):** the card offers "It did not fill" / "Confirm: I checked in ATAS and this order did not fill" for a row with a reference
  (`DashboardPage.CancelledAnswer`), relabelled in place each tick (`Ui.Relabel`; the tree is not rebuilt); no new control or `Theme` value.
- **Item 5 (`5bc37b84`):** `CONTRACTS.md` claims, with what stays NOT claimed; the `CLOSE_IN_FLIGHT` refusal and `GatewaySchema`'s `close` name the card; USER-GUIDE.
- **Judged at landing, accepted:** the landed `InFlightSettleTests` (d) `A_close_the_platform_does_not_list_settles_only_where_absence_decides_and_past_the_grace`
  — its noId arm's assertions moved to the hand-over (RECONCILING, flagged, `TRADING_PAUSED_UNRECONCILED`) BY DESIGN, the case this unit exists for; name
  kept, other arms unchanged; not a weakening. The guide's paragraph under "Two behaviours that will look like faults" (a placement deviation). Answer first:
  an ATAS stream-state row always carries a reference (`OrderKey`, at worst `ext:none/<id>`) — read from the code, NOT VERIFIED on the box.

**Verified by running (the builder, quoted; then the manager's gate):** builder at `0c8e7c65`: Release `--no-incremental` `0 Warning(s)`, `0 Error(s)`; Unit
`Passed: 1426`, Fault `Passed: 463`, 0 failed; `InFlightSettleTests` 3× 9/9, `UnconfirmedCardTests` 3× 2/2. RED before: (a) `Expected: RECONCILING Actual: WORKING`
("on the card False"); (b) the same, "the close first CLOSE_IN_FLIGHT"; (f) item 2's defect: "ten passes later CANCELLED, flagged False — never reached the
broker; a new order sent", `Expected: RECONCILING Actual: CANCELLED`. Mutant: item 2's guard dropped ⇒ (a) `Expected: RECONCILING Actual: CANCELLED`; restored.
Manager's gate at `626fe9fa` (carried to `1fa820a6`), Release: build `--no-incremental` `0 Warning(s)`, `0 Error(s)`; Unit 1426/1426 (35 s), Fault 463/463 (1 m 55 s), Integration 732/733, 1 skipped (11 m 16 s) → 0 failed.
Names vs `main`: 2247 → 2255, 0 removed, 8 added. Scan: `SCAN CLEAN`; no trailers; `rev-list --count` 0 both ways.
**CI:** branch run 37555637680 at `0c8e7c65` (on `e4f38682`): success on ubuntu-latest (12 min), macos-latest (16 min), windows-latest (48 min), package (4 min).
**Tests box:** NOT RUN — 03:22 `ready : NO - his own OFT.Platform is open; nothing of ours runs beside it`; 11:19 `ready : NO - the machine does not answer (…)`.

**NOT done, NOT verified:** the ATAS box (not granted): whether ATAS lists a filled order after a bridge drop or restart (`ICache`, `MyTrades`); the card not seen
on screen; `Errors.cs`'s CLOSE_IN_FLIGHT owner text ("There is nothing to do") unchanged though a handed-over row now waits on the owner — wording owed, light;
on the plain Simulator, where absence decides, an order it does not list is still written off "never reached the broker", as before (by design).

## 2026-10-07 — U-features landed: a feature is a spec as data known by its hash, computed by the app in decimal from only what had arrived by its instant, research-only until a licence reading confers

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-features.md` (written by seat A's read-only survey leg against `1828a188` and the
then-unlanded `u-tape-read`; re-checked against `e2f4daf8` and queued `6a058570`; dispatched `a50ef414`); resumed after the weekly stop; rebased by the builder
onto `3db0c533` and by the manager onto `6ca2a54a`, src+tests patch-id identical (`bf7edfe74311`). Merge `90aece2a` (ff-only), 5 commits (4 items, the report),
13 files, +2735/−8, all new code in `src/TradeAgent.Core/Features/`. **No schema change** (main 30, tape.db 1): no table, no reading, no op, no verb.
EVIDENCE PATH (EDGE-FACTORY § 4.3, § 6.1–6.3): every value is app code over market rows, never a model; the id is the hash of the canonical spec
(`features=1`), so a changed input is a different feature; nothing is stored, a spec proposes and the app computes; look-ahead impossible by construction (a
row counts at t only once FIRST SEEN by t − latency, source time alone never admits it); each value carries the worst class of its rows and their digest;
every tape source is research-only today, so no feature can confer live eligibility.

- **Item 1 (`3f979c8a`):** `FeatureSpec`, `FeatureCanonical`, `FeatureVersions` — a total parse; kinds `latest`, `change` (`mode` diff|ratio), `mean`, `min`, `max`,
  `pct-rank`; malformed specs refused in words; this build's tape sources only.
- **Item 2 (`69b5246a`):** `FeatureEvaluator.At`, pure and the only gate; ABSENT — never zero, never skipped over — for stale, too few, unreadable, withheld or a
  zero base; golden vectors in exact decimal.
- **Item 3 (`3c64328e`):** `FeatureSeries.Read` (audience required, through `TapeReader.Window`; ≤ 10,000 points, ≤ 50,000 rows an input, refused beyond) and
  `FeatureLicence.LiveRefusal`; the clean-history start per input.
- **Item 4 (`d22d39f1`):** `CONTRACTS.md` "Features" after "The tape"; the tape's (11) answered; "Not seeded here: the tape" and the `Database.cs` comment re-pointed
  to the conferring-dataset unit (seat A's call, 02:07: a licence reading decides only live eligibility, an absent one confers nothing, and it is read the day it is seeded).
- **Declared deviations, accepted (each keeps the purpose, several narrow it):** (1) a change's measure is the key `mode`; more refusals (a subject outside the
  six symbols, a field that is the series' time or symbol, a spec over 4,096 bytes, bounds 1 s–366 days, `min_rows` ≤ 50,000); (2) "stale" = no counted reading
  within `max_age_s`; an exponent spelling is unreadable; (3) the clean-history start is the INPUT's, found from its oldest readings within 50,000 rows (past
  that absent, never guessed); `Read` takes an optional `newest`; one test uses a lower row cap.

**Verified by running (the builder, quoted; then the manager's gate):** builder at `06499434`: Release `--no-incremental` `0 Warning(s)`, `0 Error(s)`; Unit
`Passed: 1447`, Fault `Passed: 457`, 0 failed; 3× `FeatureSpecTests` 6/6, `FeatureEvaluatorTests` 10/10, `FeatureGoldenVectorTests` 2/2, `FeatureSeriesTests` 5/5.
Mutant: the first-seen test made `row.SourceTime <= t − latency` ⇒ (a) `Expected: 1 Actual: 2`, (b) `Value = 102` expected `888` actual, (c) `Expected: False
Actual: True` — 3 of 3 red; restored byte-identical. A mutant left in place by the weekly stop was restored before items 2–4 were committed (its text in no file).
Manager's gate at `90aece2a`, Release: build `--no-incremental` `0 Warning(s)`, `0 Error(s)`; Unit 1449/1449 (37 s), Fault 463/463 (1 m 56 s), Integration 732/733, 1 skipped (11 m 19 s) → 0 failed.
Names vs `main`: 2255 → 2278, 0 removed, 23 added. Scan: `SCAN CLEAN`; no trailers; `rev-list --count` 0 both ways.
**CI:** branch run 37599960798 at `06499434` (on `3db0c533`): success on ubuntu-latest (13 min), macos-latest (16 min), windows-latest (48 min), package (4 min).
**Tests box:** NOT RUN — `ready : NO - the machine does not answer (…)` at 11:27 and 12:17 CEST.

**NOT done, NOT verified:** the fields read off a recorded tape (no home's tape opened; one dated GET of Binance's public premium-index endpoint, 2026-10-07
00:47Z, matched the fixture's shape); no op, verb or screen (`U-features-b`); multi-input kinds; cost on a months-long tape (bounded, not measured); the tape
holdout is `U-tape-holdout`'s (in flight) — whichever lands second threads it through `FeatureSeries.Read`.

## 2026-10-07 — U-vendor-limit-quote landed: the app holds at the owner's plan limit again, codex's limit sentence read with either apostrophe

Found in M0's readiness (seat M, 2026-10-07: `~/.codex/sessions` read for `usage_limit_exceeded` only, the sentences the only text taken): the vendor's sentence carried
an ASCII apostrophe in 3 events on 2026-10-01 and U+2019 ("You’ve") in 8 events on 2026-10-05/06 (Codex Desktop sessions, codex-cli 0.159.2 and 0.160.1), while
`RuntimeManifest.cs`'s pattern held the ASCII one. So on `main` a refused turn at the owner's plan limit was charged its whole reservation and nothing held the next
launch. Briefed on the orchestrator's ruling (option (a), a protection, kept off Law 10) as `docs/queue/U-vendor-limit-quote.md` (`10def272`), dispatched `30dd8b81`;
one fresh Opus builder under seat M. Rebased by `land.sh prep` from `e631e0f3` onto `18a7ab10`, src+tests patch-id identical (`defe0549a74f`). Merge `8510dfb2` (ff-only),
3 commits (items 1, 2 and the report), 3 files, +341/−1. No schema rung. Not a money-path file: the AI's spend cap and the paid-turn trigger (`U-vendor-limit`).

- **Item 1 (`4761a61f`, landed `3119a12d`):** `RuntimeManifest.cs:613` `Pattern = "You['\u2019]ve hit your usage limit"` — a C# escape, so the data is `You['’]ve…`; no IgnoreCase,
  nothing else widened; the capture comment (`:597-611`) gains the 2026-10-05/06 reading, both observed sentences and the NOT VERIFIED stdout line.
- **Item 2 (`b5d85ea4`, landed `afbd5904`):** `VendorLimitQuoteTests`, 7 tests: (i) both observed sentences through the shipped manifest — "8:49 PM" held to the end of 20:49 local on
  the read's date, "Oct 7th, 2026 1:10 AM" to the end of 2026-10-07 01:10 (the dated form, now seen in a vendor sentence); (ii) the 2026-10-01 stream with only its
  apostrophe changed (DERIVED, checked as exactly two characters, ' to ’) through `VendorLimitTests.Refusing`, as three tests: the U+2019 sentence is the last line,
  the turn is charged 0 with `context.refused`, no launch before the named minute; (iii) two guards: the ASCII recording still recognised, and a non-sentence that
  carries "usage limit" refused (written for the guard, labelled so). **Item 3:** one watched mutant, no commit.

**Verified by running (the builder, quoted; then the manager's gate).** RED on the base (`30dd8b81` + item 2): `Failed: 5, Passed: 2, Total: 7` — `:94`, `:108`
"Assert.NotNull() Failure: Value is null"; `:157` Expected "You’ve hit your usage limit. Upgrade to P"··· Actual "OpenAI Codex CLI did not finish: Reading "···; `:179`
"Expected: 0 Actual: 1.28"; `:240` "The collection contained 2 items"; the guards green. Mutant (the ASCII-only pattern restored): `VendorLimit*` `Failed: 5, Passed: 16,
Total: 21`, the same five; restored, `git diff` empty. Builder's gate via `suite.sh`: Release `--no-incremental` 0 Warning(s), 0 Error(s), 19 projects; Unit 1433/0;
Fault 463/0; `VendorLimit*` 3× 21/0. Manager's gate at `8510dfb2` (the rebased tip), Release: build `--no-incremental` 0 warnings, 0 errors; Unit 1456/1456 (9 m 8 s), Fault
463/463 (1 m 57 s), Integration 732/733, 1 skipped (11 m 19 s) → 0 failed. Names vs `main`: sets 2278 → 2285, 0 removed, 7 added. Scan clean; no trailers; rev-list 0.

**CI, judged (the landing manager, on the orchestrator's leave to land with the reds recorded).** Branch run 37604406951 at `b5d85ea4`: ubuntu ✓ 13 m, macos ✓ 16 m,
windows ✗ 62 m on `QuoteClockTests.A_bar_fed_feed_is_not_degraded_one_bar_after_its_close` (`:309`, "simulator at 40 s : READY —"). Re-run 37612881764 at `e631e0f3`
(docs over the same code): ubuntu ✓ 12 m, macos ✓ 16 m, windows ✗ 112 m — Unit 1431/1432 took 1 h, Fault 456/458, Integration 640/642 + 1 skipped. The windows reds:
`ResumeOnStartTests.A_restart_with_the_ai_working_starts_its_runtime_and_the_next_due_wake_is_taken_without_a_press` (1 m 48 s, `Until` at `:357`);
`QuoteClockTests.A_bar_fed_feed_…` again (41 s) and `Both_quote_gates_and_the_decision_gate_read_the_same_clock` (21 s); `BridgeLivenessClockTests.A_bridge_pulsing_while
_the_wall_clock_steps_forward_an_hour_stays_ready` (224 ms). JUDGEMENT: none is reachable through this unit's code, a regex in codex's manifest that matches one vendor
sentence. The QuoteClock pair is the known simulator-clock rig fault, its third and fourth sightings, owed to `U-test-hygiene-2` (built, not landed). BridgeLivenessClock
is a first sighting, routed to a survey by the orchestrator. ResumeOnStart was green at 37604406951 on the same code; this diff's only reach to it is load (three more
child-process tests in the same assembly), on a runner whose Unit suite took 1 h. Each is recorded as a sighting with its run, not as a pass. Package skipped in both runs.
Tests box: NOT RUN — `ready` exit 1 at 11:57, 12:00, 12:28 and 13:13 ("NO - the machine does not answer").

**M0:** M0's build is `1828a188` with this unit's item 1 cherry-picked as `4a06a8d0` (local, detached, never pushed; patch-id `d198686b6d67` = `4761a61f`'s): a
protection, not an input (Law 10's pin stands). **NOT done, NOT verified:** that `codex exec --json` stdout carries U+2019 under 0.160.1 (the session log's
`payload.error.message` does); "try again later." still has no observed sentence; the app run (M0 is where it meets a limit, if it does); no box.

## 2026-10-07 — U-test-hygiene-2 landed: the windows-only reds main kept showing in tests no diff reaches are fixed as a class on seams, not the runner's clock, and the pipe's token is written once, owner-only from its first byte

Built by one fresh Opus builder under build-fleet seat P from `docs/briefs/U-test-hygiene-2.md` (seat P's brief `192a60ab`, re-checked `cab53474`, item 6 folded
in `e4f38682`; dispatched `3db0c533`, returned and re-dispatched `06851afd` on the orchestrator's rulings); **landed by seat A on the orchestrator's reassignment
(2026-10-07 16:15) while seat P is closed.** Rebased onto `d33d3419` by the manager, src+tests patch-id identical (`98b6139da241`). Merge `db2d683b` (ff-only), 7 commits (6 items,
the report). **No schema change (30).** Protections touched: a CREDENTIAL (the pipe's `ipc.token`), the COID witness's retry bound (money path), the press
deadline's simulator rig. The reds it answers on `main`: `CoidWitnessTests` (37443797669), `VenueOpsTests` `ipc.token` IOException (37443989301),
`SweepRequestIdTests` five-order sweep (37494849714) and its NRE at :333, `OperatorPressIsAnEmergencyTests` (37604316193), `QuoteClockTests` (37600211089).

- **Item 1 (`82613016`):** `IpcToken.Ensure` — a usable token returns at once; else an in-process lock, a re-read, and `SecretStore.Write`: a temp created new (on Unix
  0600 from its first byte), flushed, renamed over; `Read` shares write+delete. RED before 4/4: "16 first callers hold 16 different token(s) … readers saw a
  part-written file 2 time(s)". Mutant (lock and re-read removed): "16 first callers hold 16 different token(s)". DEVIATION, accepted: no lock-free create-once —
  `File.Move(…, overwrite: false)` measured NOT atomic on macOS (2284 of 3000 rounds had more than one winner); across processes the product's two writers take
  `SingleInstanceLock` before `Ensure` (`AppHost.cs:563`, GatewayHost `Program.cs:18`) — by reading, NOT run.
- **Item 2 (`ba617cb7`):** the witness's waits come through a `backoff` seam (default `Thread.Sleep`); attempts, backoff and budget unchanged; the bound is asserted on
  the waits the product ASKS for (`waits.Sum() < 2000`), slept for real; `Assert.Equal(5, attempts)` and the stopwatch `>= 150` unchanged. Mutant
  (`ReplaceBackoffMs << (4 * attempt)`): "the retry asked to wait 1398080 ms … — the budget is not bounded". JUDGED: the brief's design (seams, not the runner's
  clock), not a loosening — the mutant proves the bound still bites; the witness's lock and sidecar sleeps stay unseamed (named).
- **Item 3 (`0c6840ca`):** the five-order sweep's first wave released by a latch at the wire instead of 750 ms of simulator latency; the assertion unchanged.
- **Item 4 (`8a965de2`), extended:** `Answered(reply)` asserts `Ok` before reading `Data` at :339 and the class's four other deadline-bound sweeps (NRE → words).
- **Item 5 (`5f5d832b`):** `FakeBroker.Clock` (default `TimeProvider.System`) stamps orders and fills; `RiskGateTests` and `QuoteClockTests` on one clock.
- **Item 6 (`49373d5d`):** the simulator's last predicted wait stops at the operation deadline as its other wait does; new test, RED before 3/3 ("… still on the
  platform 2203 ms after the deadline the press itself opened …"); mutant (`await Sleep(left, ct)`) red, restored. `Timing` trait unchanged.

**Verified by running (the builder, quoted; then the manager's gate):** builder at `081c0e98`: Release `--no-incremental` "0 Warning(s) 0 Error(s)"; Unit
`Passed: 1427`, Fault `Passed: 464`, 0 failed; touched classes 20× each, every run green (`IpcTokenTests`, `RiskGateTests`+`QuoteClockTests`,
`OperatorPressIsAnEmergencyTests`, `CoidWitnessTests` 149, `SweepRequestIdTests`+`ReplayedSweepSendsNothingTests` 48).
Manager's gate at `db2d683b`, Release: build `--no-incremental` `0 Warning(s)`, `0 Error(s)`; Unit 1457/1457 (9 m 7 s), Fault 464/464 (2 m 2 s), Integration 732/733, 1 skipped (11 m 14 s) → 0 failed.
Names vs `main`: 2285 → 2287, 0 removed, 2 added. Scan: 35 hits, every one the pipe token's or a cancellation token's NAME or prose about them (no secret literal in the diff: the token is minted by `RandomNumberGenerator` at run time) — excluded by name in the check and the record; no trailers; `rev-list --count` 0 both ways.
**CI:** three runs at `081c0e98` (on `6ca2a54a`): 37605552055, 37605555760, 37605559448 — success on ubuntu-latest, macos-latest, windows-latest and package in
all three; in the third, windows' `Timing` step red on its FIRST attempt in `GatewayPipeBackpressureTests.A_close_all_wave_that_disposal_lands_in_leaves_nothing
_unsettled` :1443, green on the category's second attempt (the doctrine's one second attempt) — a class this unit did not touch; the same first-attempt red at
CI 34715391501; judged not moved by item 6 (reached only with < 500 ms left of its 12 s budget). **Tests box:** NOT RUN — `ready : NO - the machine does not answer (…)` 12:10.

**NOT done, NOT verified:** item 1 on Windows by CI only, its cross-process half by reading only; the new press test's margin on a hosted runner not measured;
the witness's lock and sidecar sleeps; `ConnectorSendDeadlineTests`; `BridgePipeAuth.WriteFile` (`AtasConnector.cs:2091`) writes `bridge.auth`'s temp before
restricting its mode — the window item 1 closed for `ipc.token`, now surveyed by seat A for a fix brief.

## 2026-10-07 — U-tape-holdout landed: no caller on the agent-facing pipe reads a tape row from inside any dataset's holdout window — refused in words, never clipped — and the referee still does

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-tape-holdout.md` (written by seat A's manager on the orchestrator's ruling of
2026-10-07 12:02 — U-tape-read had made the tape readable by every role and no tape holdout existed; queued `ce613da8`, dispatched `6ca2a54a`); rebased by
the builder onto `18a7ab10` (over U-features, so it threads the holdout through `FeatureSeries.Read`) and by the manager onto `ad3dc494` (over U-test-hygiene-2, rebased and gated while a GitHub push outage held `ad3dc494` local; prep then found it on main and the gate carried),
src+tests patch-id identical (`22023327a0c3`). Merge `0ea25bf4` (ff-only), 7 commits (3 items, the threading, two follow-ups, the report), 16 files, +1025/−89.
**No schema change.** EVIDENCE: the holdout (`Holdout.cs`; `docs/COUNCIL.md` "holdout data the research process cannot reach") now covers the tape.

- **Item 1 (`c5e96c28`):** `TapeHoldout` beside `Holdout` — every dataset holding a cutoff holds a tape window `[holdout_from, LastBar + one Interval)`, open campaign
  or not; built for the pipe from the dataset ledger at every read; the referee's pass-through `internal`.
- **Item 2 (`7cf47946`, `a5e15a57`):** `TapeReader.Window` and `TapeStore.AsOf` take it as a REQUIRED argument and refuse INSIDE: a read whose source-time window
  reaches any window is refused in words naming the dataset, cutoff and window — never clipped — whatever the source, series or subject; an as-of read is refused
  when its row lies inside one; `data-tape` hands the pipe's; `FeatureSeries.Read` requires it too (U-features landed first).
- **Bounded start (`3976402a`, `62081749`), seat A's ask:** where the clean-history search stops at a window's close, the answer says the start was bounded by a
  holdout (`FeatureCleanStart.Bounded`), found or absent — never read as the input's first reading.
- **Item 3 (`5ee2f0aa`):** `CONTRACTS.md` THE HOLDOUT in the tape's section; `WorkspaceBuilder` and `GatewaySchema` tell agents how to ask.
- **Declared deviations, accepted:** (1) `TapeHoldout` carries the audience with the ledger and replaces `BarAudience` on the tape readers; (2) a dataset with no
  bar, or whose cutoff is at or past its last bar's close, holds no window; one with no recorded last bar or an unreadable interval is held with NO end (the
  conservative side); (3) while any cutoff is set, a `data-tape` read with no window is refused (the brief's unbounded rule); agents are told how to ask.
- **Answer first:** no in-process tape read besides the referee's needs the holdout — `GdeltRecorder`'s reads are bookkeeping; `data-list`, `status` and the report
  give counts, names and arrivals, never a value; `FeatureSeries.Read` now requires it; the referee reads no tape today.

**Verified by running (the builder, quoted; then the manager's gate):** builder at `3c160413`: Release `--no-incremental` 0 warnings, 0 errors; Unit `Passed 1458,
Failed 0`; Fault `Passed 463, Failed 0`; 3× `TapeHoldoutTests`, `TapeStoreTests`, `TapeReadTests`, `TapeAnnouncementTests`, `GdeltRecorderTests`, `FeatureSeriesTests`
36/36 each run. RED before at `6ca2a54a`: (a) "a tape read reaching dataset 1's holdout window was served to operations" (8 rows); (d) "an as-of read served
operations a row stamped 2026-08-15 12:01:00Z, inside the holdout window". Mutant (Window's refusal line removed) ⇒ (a) red, "served 10 rows"; restored.
Manager's gate at `0ea25bf4`, Release: build `--no-incremental` `0 Warning(s)`, `0 Error(s)`; Unit 1466/1466 (9 m 12 s), Fault 464/464 (2 m), Integration 732/733, 1 skipped (11 m 13 s) → 0 failed.
Names vs `main`: 2287 → 2296, 0 removed, 9 added. Scan: `SCAN CLEAN`; no trailers; `rev-list --count` 0 both ways.
**CI:** run 37613808298 at `88c51702`: success on all four jobs. Run 37635095478 at `3c160413` (88c51702→3c160413: one sentence in `FeatureSeries.cs`, one ETHUSDT
assertion, one CONTRACTS clause): ubuntu and macos success; windows red ONLY in `BridgeLivenessClockTests.A_bridge_pulsing_while_the_wall_clock_steps_forward_an_hour
_stays_ready` ("The stream is currently in use by a previous operation on the stream." at `StubBridge.Heartbeat`, `Harness.cs:193`), package skipped. **JUDGED
(seat A): a test-rig race, not this unit** — the stub bridge writes heartbeat and answers on one writer with no turn-taking (the real `BridgeServer` serialises);
read as RIG by seat A's survey at 16:38 (owed: `U-test-hygiene-3` item (b)); this diff touches no bridge, connector or harness file; no re-dispatch under the throttle.
**Tests box:** NOT RUN — 17:14 `ready : NO - his own OFT.Platform is open; nothing of ours runs beside it` (exit 5).

**NOT done, NOT verified:** no named-pipe test (a) goes through `CallAsync`, the same handler; no every-op sweep for tape rows; `Errors.cs`'s `HOLDOUT_WITHHELD`
owner text still says "bars" (wording, light); no referee tape door yet (v2a's).

## 2026-10-07 — the observed run, attempt 3: a real model authors, backtests and retires three versions in six unattended hours; no verdict, so no paper trade; U-resume-agent verified

`docs/briefs/U-observed-loop.md`, run by seat M through the app's own UI on this Mac (background screen control on `dev.tradeagent.mac`). The milestone is NOT claimed: no
verdict was requested, so nothing was allocated, deployed or filled. Every line is a press, a read-only database read, a `trade` read or a quote; the brief is retired with it.

- **Build:** GO `1828a188` (pinned under Law 10) plus `U-vendor-limit-quote`'s item 1 (landed `3119a12d`, record `d33d3419`, landing CI 37638355373 ✓ ×3 + package)
  cherry-picked as `4a06a8d0` (local, detached, never pushed; patch-id `d198686b6d67`) — a protection, not an input. Debug bundle, sha256/16 TradeAgent.dll
  `08f9ff8e207ddb84`, Core `11d75b8344a4a5f1`, Gateway `c579a920241e3dd6`, AgentRuntime `8b95bdcee8c7420b`; codex-cli 0.160.1, `codex login status` exit 0.
- **Home and first start (14:38:47Z):** `observed-run-home`, schema 26 → 30 at the first start (`Database.Migrate` keeps rungs 27–30), PAPER-1, dataset 1 (binance-spot
  BTCUSDT 1m, holdout from 2026-06-01), campaign 1 with 0 verdicts, envelope to 2026-10-08T15:47Z. 14:38:50Z: "Instrument check: BTCUSDT verified against Binance spot's
  published instrument definition: tick 0.01, step 0.00001, minimum quantity 0.00001, minimum notional 5", so no "Check now". The tape recorded from 14:38:49Z.
- **Bounds stated before the work press:** T0 + 6 h inside the local day; the 5 USD daily cap; the owner's plan (its limit ends the run); the paper envelope; paper only.
- **Presses, all of them:** the gpt-6-luna model pill (14:42:11Z "The AI now runs on gpt-6-luna"; clipped at the window's edge, pressed inside its visible part); "Start the
  AI"; "Let the AI work on its own", armed "Confirm: let the AI keep working without being asked, up to 5 USD a day" and confirmed — T0 = 14:42:50.516Z "The AI was set
  to work on its own". Process actions: the launch; one quit from the app's menu and relaunch; the dead-man's quit. Nothing seeded, no file handed over.
- **U-resume-agent VERIFIED in the running app:** quit between turns at 15:32:42Z (0 LAUNCHED), relaunch at 15:32:54Z, and with no press the research wake due 15:34:55.2006Z
  launched a gpt-6-luna turn at 15:34:55.2647Z.
- **The model's work, before the holdout (dataset 1, 2025-10-01 → 2026-05-31, under the venue model's 0.1 % fee and 0.02 % assumed slippage):**
  - Operations froze `ef4969c5` (`bars 1h`, RSI(14) < 30 in, > 55 out, stop 2 %, target 1 %): 103 trades, net −1,007.03.
  - Research reviewed it on Operations' brief, "Recommendation: RETIRE", and proposed "change only the profit target from 1% to 2%". Operations froze `9677ccc7`: net −708.07,
    retired as well.
  - Research froze `f984bacc` (an hourly channel breakout): 65 trades, net −2,115.76.
  - Operations' last plan (20:14:12Z, the agent's claim): "MA/SMA crossover, hourly RSI, and hourly Donchian candidates remain retired after negative pre-holdout results.
    No verdict has been spent."
- **Turns and cost:** 36 turns, all ENDED and exit 0 — operations 19 for 2.0041 USD, research 17 for 1.8403. Total 3.8444 USD of the 5 USD cap, priced at the requested
  gpt-6-luna (the CLI never named its model). Tokens: in 224,121,055 (216,578,560 cached), out 1,848,823. No vendor limit was met: `~/.codex/sessions` holds no
  `usage_limit_exceeded` after T0.
- **End:** the dead-man: "20:43:16Z deadline: quitting the app (pid 24913; LAUNCHED attempts at quit: 0)", "20:44:17Z app exited" — between turns, NOT paused
  (`ai_works_on_its_own`, `resume_ai_on_start` still 1: this home's next launch resumes the AI by itself). No orphan (`ps`, `lsof` at 20:49Z; the owner's Codex Desktop
  left alone). The clean close removed `-wal`/`-shm`, so `sqlite3 -readonly` answers "(14)"; the end-state reads use `?mode=ro&immutable=1`.
- **Tape at the end:** 6,891 observations from 4,592 fetches up to 20:43:03Z (Binance USDⓈ-M context, OKX EEA announcements: 20, GDELT crypto rows: 1,112). The run's
  one warn line: "GDELT news: the backfill asked for 525715438 bytes today, and it asks for at most 524288000 a UTC day".
- **Gaps found:** (a) no read-only verb serves a completed run's per-trade trace — Research: "No retained per-trade rows or read-only command to retrieve a
  completed run's trace were available"; (b) waiting costs money: after the last brief/report (15:54:58Z) 16 review turns (16:10–20:15Z) spent 2.0337 USD, 53 % of
  the run, while Operations' plan read "No agenda or wake is pending"; (c) Research's claim: "55 signals unfunded", `risk_fraction` "has no cash-notional cap"; (d) no
  role asked `trade verdict`, so the verdict and paper path went unexercised.
- **What did NOT happen:** a verdict, promotion, allocation, deployment, op, order or fill; a decision on fills; a vendor limit, so U-vendor-limit-quote is not seen
  working in the app; "Close all positions"; any live mode; Windows; the box. The CLI runs unsandboxed: no verdict of this home would be protected evidence.

## 2026-10-07 — U-test-hygiene-3 landed: four windows-only test-rig reds of 2026-10-07 made deterministic or decidable — a temp held inside its grace, one writer on the stub bridge's pipe, a quote on the gateway's clock, and a resume-on-start wait that names the loop's state

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-test-hygiene-3.md` (written by seat A's read-only survey of 2026-10-07 16:38, which
read each red as RIG — the product right — or UNKNOWN; queued `a8bee682`, dispatched `901586a7`), landed by seat A on the orchestrator's lane reassignment while
seat P is closed. Re-prepped over seat M's M0 record (docs only; the gate carried), src+tests patch-id identical (`c746333e8b11`). Merge `16a0feb2` (ff-only), 5 commits (4 items, the report). **Tests and harness only: `src/` unchanged** (every
mutant restored, `git diff main -- src` empty). **No schema change.** No assertion loosened, no test moved into `Timing`, no deadline raised.
The reds it answers: `CoidWitnessTests.Two_writers_do_not_share_a_temp_name` (37611591775, `main` `34a34ba0`), `BridgeLivenessClockTests.A_bridge_pulsing_while_the_wall
_clock_steps_forward_an_hour_stays_ready` (37612881764, 37635095478), `QuoteClockTests.Both_quote_gates…` (37612881764), `ResumeOnStartTests.A_restart_with_the_ai_working…`.

- **Item 1 (a) (`f2eecac5`):** the first writer's stranded temp is held inside the quarantine grace for the test's length (dated an hour ahead before B writes), and
  B's answer is now asserted (`Assert.False(Submit(b, "TA-B"))`, its failure not "another writer owns"), so a refused lease and the grace race print differently.
  Before: the old body red on this Mac "Expected: 2 / Actual: 1" — windows' own line. The survey's reading stands: no clock is in a temp's name (a GUID session).
- **Item 2 (b) (`7fa3d47a`):** `StubBridge.Send` holds one `SemaphoreSlim(1, 1)` across each whole write, as the real `BridgeServer` does; `DisposeAsync` takes it
  before disposing the writer. New `StubBridgeTests.A_send_waits_for_an_answer_in_flight`. Before: red 3/3 "the pulse was written while the loop's answer was still
  being written"; a scratch 100 ms after the latch reproduced windows' exception "The stream is currently in use by a previous operation on the stream".
- **Item 3 (c) (`47827cce`):** the quote is stamped on the gateway's clock (`QuoteClock = clock`, `QuoteAge` 5 s); expectations unchanged. Before: red at :236 "ok —
  FILLED" — windows' own line; after: green, and green with a real 16 s delay.
- **Item 4 (d) (`6e954d40`), the orchestrator's ask:** `Until` takes the loop's state and fails with it at the same 60 s deadline — whether a turn took the wake and
  when it launched, `presence.LastAliveAt`, the loop's error count and next turn; shown once by a probe made to hang its turn (scratch, not committed). The windows
  red stays UNKNOWN until its next sighting, which now names its shape.
- **Found in item 4's scratch runs, NOT this unit (passed to the orchestrator, surveyed at once):** a hung turn's agent process (`/bin/sh probe.sh` + `sleep 300`)
  OUTLIVED `Mission.PauseAsync` and `Agent.StopAsync`, orphaned (ppid 1), three times out of three — a survey leg reads whether the product does that.

**Verified by running (the builder, quoted; then the manager's gate):** builder at `b999a588`: Release `--no-incremental` "0 Warning(s) 0 Error(s)"; Unit 1466,
Fault 464, Integration 733 + 1 skipped, 0 failed; each touched class 10×, every run "Failed: 0".
Manager's gate at `9de2622d` (carried to `16a0feb2`), Release: build `--no-incremental` `0 Warning(s)`, `0 Error(s)`; Unit 1466/1466 (9 m 7 s), Fault 464/464 (2 m), Integration 733/734, 1 skipped (11 m 15 s) → 0 failed.
Names vs `main`: 2296 → 2298, 0 removed, 2 added (the second "added" name is `LatchedStream.Release()`, a method, not a test — disclosed; [Fact]/[Theory] +1). Scan: `SCAN CLEAN`; no trailers;
`rev-list --count` 0 both ways.
**CI:** two runs at `b999a588`: 37675881283 (windows 49 m, ubuntu 13 m, macos 14 m, package) and 37675884979 (ubuntu 13 m, windows 58 m, macos 15 m, package) —
success on every job in both; no `Timing` retry file in either (green on the first attempt).
**Tests box:** NOT RUN — 22:06 `ready : NO - his own OFT.Platform is open; nothing of ours runs beside it` (exit 5).

**NOT done, NOT verified:** item 4's class on the tests box beside the full suite; whether (a)'s windows red was the grace race or a refused lease (both printed
"Actual 1"; the new body tells them apart); `QuoteClockTests.A_bar_fed_feed…` was hygiene-2's item 5, not this unit's.

## 2026-10-07 — U-bridge-auth-owner-only landed: the bridge pipe's shared key is owner-only from its first byte on macOS and Linux, published by one rename, and on Windows a reader holding it no longer refuses its rewrite

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-bridge-auth-owner-only.md` (written by seat A's read-only survey of 2026-10-07
16:38, which found the 0644-before-chmod window; queued `a8bee682`, dispatched `923fcb57`; a seat-P-lane item kept by seat A to its landing on the orchestrator's
ruling); rebased by the manager onto `6bd92855` (over U-test-hygiene-3 and M0's record), src+tests patch-id identical (`2f663fd23e58`). Merge `40b0036c` (ff-only),
5 commits (3 items, a declared item 4, the report), 4 files, +329/−20. **No schema change (30).** CREDENTIAL (`CLAUDE.md`): who can read the bridge's shared key,
and when. The handshake is unchanged: path, JSON, the key's lifetime, `Proof`, `BridgeProtocolVersion` 3 and `Versions.BridgeCompatible`'s exact match.

- **Item 1 (`b7158266`):** a seam in the write at the instant the key sits in the temp, before anything else touches it; test (a) red at this item.
- **Item 2 (`1170532a`):** the write takes `SecretStore.Write`'s shape inside `BridgePipeAuth` (no new assembly in the bridge DLL's closure): a temp of the call's
  own, `CreateNew`, `FileShare.None`, `UnixCreateMode` 0600 off Windows, flushed, renamed over, deleted when the rename did not consume it; `Restrict` gone.
- **Item 3 (`56063045`):** the reader opens sharing `ReadWrite | Delete`. MEASURED INSUFFICIENT on Windows: windows-latest still refused the replace ("Access to
  the path is denied") while a reader held the file, with either sharing — `File.Move` (`MoveFileExW`) refuses a replace under ANY open handle.
- **Item 4 (`ecbd4255`), a declared DEVIATION, ACCEPTED (seat A; the orchestrator's view the same):** on Windows the publish is `FileRenameInfoEx` with
  `FILE_RENAME_FLAG_POSIX_SEMANTICS` (kernel32 P/Invoke inside `BridgePipeAuth`), so readers keep reading while it is replaced; it falls back to `File.Move` ONLY
  on errors 1, 50 and 87 (invalid function, not supported, invalid parameter — a volume without the POSIX rename), and ANY other refusal throws as before
  (refuse, never guess — read in the code at `AtasConnector.cs` `ReplaceWhileReadersRead`). (b) green on windows-latest in 37682749524.
- **Deviation 2, accepted:** (a)'s Windows arm reads the owner-only claim off `%LOCALAPPDATA%`'s DACL (read, never written), beside "the temp at the seam equals
  the published file" and "no entry of its own" — not off the test home, whose `TEMP` on the tests box inherits `C:\`'s broader entries (inferred, NOT checked).
- **The brief's STOP rule did not fire:** (a)'s Windows DACL arm green in all three windows-latest runs (37675671465, 37675951756, 37682749524).

**Verified by running (the builder, quoted; then the manager's gate):** builder at `511fd627`: Release `--no-incremental` 0 warnings, 0 errors; Unit 1466, Fault 464,
0 failed; `BridgePipeAuthTests` 3× 16/16; `BridgeRoundTripTests` 41, 0 failed. RED before, this Mac at item 1: (a) "the secret was on disk at 0644 before the rename
and is published at 0600 … umask 0022"; (c) "the refused rewrite left bridge.auth.44184.tmp beside bridge.auth"; run 37675671465: (a), (c) red on ubuntu and macos,
(b) and (c) on windows — (b) "UnauthorizedAccessException: Access to the path is denied". Mutant (`UnixCreateMode` dropped) ⇒ (a) "published at 0644"; restored 16/16.
Manager's gate at `40b0036c`, Release: build `--no-incremental` `0 Warning(s)`, `0 Error(s)`; Unit 1466/1466 (9 m 7 s), Fault 464/464 (2 m), Integration 736/737, 1 skipped (11 m 14 s) → 0 failed.
Names vs `main`: 2298 → 2301, 0 removed, 3 added. Scan: 24 hits, every one the word "secret" in prose, a test name, the `Secret` property or the `Secret('a')` helper (64 characters built at run time; no literal key or hex value in the diff, checked) — excluded by name at check and record; no trailers; `rev-list --count` 0 both ways.
**CI:** run 37682749524 at `511fd627`: success on ubuntu-latest, macos-latest, windows-latest (51 min) and package — each test job Unit 1465, Fault 458,
Integration 644 + 1 skipped, Timing 1/6/91, 0 failed, no retry. **Tests box:** NOT RUN — 23:29 `ready : NO - the machine does not answer (…)` (exit 1).

**NOT done, NOT verified:** the fallback path (a volume without the POSIX rename) and the 32-bit `FILE_RENAME_INFO` layout never ran (CI is x64 NTFS; the writer is
TradeAgent's 64-bit process, the 32-bit bridge only reads); no ATAS box, no app run; a home outside the user profile inherits its directory's DACL — an explicit DACL
at creation stays the owner's question (on the board); `SecretStore.Write` keeps `MoveFileExW` and its comment that sharing Delete lets a replace through is MEASURED
FALSE — routed to seat P as a light item. No `bridge.auth.<pid>.tmp` of an older build found on this Mac (the M0 home not looked into).

## 2026-10-08 — U-language-v2a landed: a program reads a feature it declares, bound into its id by the feature's hash, valued at each bar's close from only what had arrived, refused by any reader that cannot run it — every v1 text and id unchanged

Built by two fresh Opus builders under build-fleet seat A from `docs/briefs/U-language-v2a.md` (written by seat A's read-only survey at `30dd8b81` against the
then-unlanded `u-features`; queued `b73b8855`, re-checked `d2e17037`, dispatched `18a7ab10`): the first paused on the orchestrator's usage throttle (2026-10-07
16:46, its WIP and a `## Paused` note committed), the second continued from the branch (a usage pause, not a failure — no fresh-fixer question). The second
split the WIP before rebasing, then rebased over U-tape-holdout onto `6bd92855` (one conflict, `TapeStore.cs`: main's quarantine paragraph kept, the referee's tape
read folded in); rebased by the manager onto `d73ecd59`, src+tests patch-id identical (`d3c5c7e8ef2a`). Merge `44cd1c16` (ff-only), 7 (5 items, the pause note, the report) commits. **No schema change.**
EVIDENCE PATH (EDGE-FACTORY § 4.4, § 6.1–6.2): no model output on the signal path (values are `FeatureEvaluator`'s over market rows); evidence binding (a
declared feature's id is in the program's identity, and a run id adds a SHA-256 over every value read); no look-ahead by construction; the holdout carried into
feature reads; research-only inputs confer no live authority (every tape source today).

- **Item 1 (`8f2308d1`):** `feature <name> = <spec JSON>`, a declaration never reserved; canonical `feature <name>=<id>` lines only when declared, so the identity names every
  input hash; `StrategyVersions.Manifest`, `LanguageVersion` and `program/1` do not move — every v1 text, canonical form, id and golden vector unchanged (shipped ids);
  the two visitors that failed OPEN (`StrategyCanonical`'s `"?"`, `StrategyWarmUp`'s `1`) now throw, so `Parse` refuses.
- **Item 2 (`3fde795b`):** `StrategyProgram.Requires` — the declaration kinds a program uses, each required; readers name what they implement and refuse the rest in words:
  the paper runner refuses `feature` before its first bar (`CannotRun`, no replacement) — lifted by `U-runner-features`.
- **Item 3 (`d3e947d7`):** the evaluator is handed each declared feature's value as it had arrived by the evaluated bar's close, read under the run's audience in
  deterministic slices; absent → no decision; a `Feature` trace line per feature (id, clean-history start, bars before it, absent, worst class); (p) pinned.
- **Item 4 (`4b2a7170`):** `Standing` re-reads a version whose canonical holds a `feature` line — another id is INVALIDATED in words; its live refusal is each feature's.
- **Item 5 (`d4209d60`):** `STRATEGY-LANGUAGE.md` Features, the agents' paragraph, `CONTRACTS.md`.
- **Declared deviation, accepted (the holdout's API, landed beside it):** `Backtest.Over` keeps its `BarAudience` and builds the feed's `TapeHoldout` from it with the
  same ledger (a new internal `TapeHoldout.Of`; `Pipe` stays the only public maker), so ONE audience decides bars and features; a run whose features would reach a
  holdout window is refused before a bar is read (`HOLDOUT_WITHHELD`, nothing charged or recorded), a cutoff set mid-run halts at the next slice; a bounded
  clean-history start rides on the `Feature` line (`clean_history_bounded`). (i)'s pipe backtest now ends an hour before the cutoff, assertions unchanged.

**Verified by running (the second builder, quoted; then the manager's gate):** at `065b7cbe`: Release `--no-incremental` `0 Warning(s)` `0 Error(s)`; Unit 1485, Fault
465, 0 failed; `FeatureProgram*` + `EvaluatorLimitTests` 3× 30/30, `FeatureProgramRunnerTests` 3× 1/1. RED before: the two new holdout tests on a naive port —
`Assert.Throws() Failure: No exception was thrown`, `Assert.StartsWith() Failure … String: null`; (m), (n) at item 3 — `Expected: "invalidated"`, `Expected: "feature
d9f3e14e5dd8 reads binance-um-pre…" Actual: null`. Mutant: `Backtest.Run` asking the NEXT declared close ⇒ (f) `Expected: COMPLETED Actual: FAULTED`; restored.
Year benchmark: 8,760 hourly bars over 525,600 minute bars and 525,600 tape rows `COMPLETED` in 11.8 s, again 10.4 s (1,142 trades).
Manager's gate at `44cd1c16`, Release: build `--no-incremental` `0 Warning(s)`, `0 Error(s)`; Unit 1485/1485 (9 m 8 s), Fault 465/465 (2 m 3 s), Integration 736/737, 1 skipped (11 m 22 s) → 0 failed.
Names vs `main`: 2301 → 2321, 0 removed, 20 added. Scan: one hit, `StrategyParser.cs`'s `new Expressions(line, tokens, constants, indicators, features)` (a constructor handing on the lexer's
output), excluded by name; no trailers; `rev-list --count` 0 both ways.
**CI:** run 37689095058 at `065b7cbe` (on `6bd92855`): success on windows-latest (49 min), ubuntu-latest (12 min), macos-latest (15 min), package (3 min).
**Tests box:** NOT RUN — 23:46 `ready : NO - the machine does not answer (…)`.

**NOT done, NOT verified:** a `Feature` line's clean-history facts are not in the run id (two pipe runs of one id under different holdout windows can state different
starts; the ledger keeps the first — written into CONTRACTS); the paper runner's feature support (`U-runner-features`); Integration locally (CI ran it); no app run.

## 2026-10-08 — U-press-close-once landed: the owner's Close all asks the platform about a same-side market close still in flight before each leg sends, and closes over an order it cannot decide only on his explicit second press

One fresh Opus builder under seat P built it from `docs/briefs/U-press-close-once.md` (draft from seat P's survey at `bdf5affa`; re-checked by seat P at `cc7a0974`, queued
`c64d9d3a`, dispatched `0dae7079`). Owed BEFORE ANY LIVE USE (`U-close-once`, `U-inflight-settle`). Merge `698e63d7` (ff-only), 4 commits (red-first tests `f08ed131`, item 1
`6ed9bff4`, item 2 `30a0a6af`, report `698e63d7`), rebased by seat P's prep onto `78be3e9d` then `1891074e` with an identical src+tests patch-id. No rung. MONEY PATH (gateway):
the emergency press must not send a close beside one still working (a long becomes a short), nor close over an order without a final answer silently (`CLAUDE.md` rule 3).

- **Item 1:** `CloseCapturedAsync`, `ClosePress` legs only, after the UNKNOWN settle → `AskAboutTheClosesInFlightAsync`: every non-DISPATCHING `ClosesInFlightOn` row through
  seat A's `SettleAnOrderInFlightAsync` alone, the press's deadline checked per row. (a) settled, nothing filled → sends; (b) listed live → cancelled, asked again, else refused
  on every press; (c) another press's row → waits, no row; (d) undecided → the `unsettled`-shape flagged row, THEN `AddKvOnce("press_told:{request}")`, and a press with
  another nonce finding it still undecided closes over it, named; (e) filled → said as drift, nothing sent. The app's legs are untouched.
- **Item 2:** the summary names every waited, refused, told, closed-over or cancelled order and the one action that ends it; the (d) row says what a second press does and
  its risk ("should it still be working and fill, ES ends the other way by up to 2"); `docs/CONTRACTS.md` turns the NOT-claimed paragraph into a claim with its limits;
  `docs/USER-GUIDE.md` (Close all); `Errors.cs` CLOSE_IN_FLIGHT no longer says "There is nothing to do" (seat P's owed light item, closed here).
- **Deviations, ACCEPTED by seat P:** (1) press rows first and every other row asked before any cancel, so a leg that will not send cancels nothing; (2) (d) is the complement
  of (a)/(b) — an absence inside the clock or a row moved while asking is (d), a row the stream made final meanwhile is settled; (3) a press settle marks the fill pull due, as
  the sweep's does; (5) `InFlightSettleTests`' helpers made `internal`. **Judged exception (4):** `SecondPressRefusedTests.A_second_close_all_is_refused_while_the_first_is_unresolved`
  — its second half asserted the doubling this unit removes (`Assert.Equal(2, c.Closes)`, a second close beside press 1's resting close, case (c)); it now asserts 1 close,
  the resting row named, flat once it fills; name and first half unchanged; disclosed in the report.

**Verified by running (the builder, quoted).** RED before item 1, on the base binaries: (b) "sells at the wire : 1 before the press, 2 after", "ES -2", `Expected: 0 Actual:
-2`; (b, never closed over) `Expected: 0 Actual: 1` closes; (c) "closes at the wire : 1 before, 2 after"; (d) press 1 closed ES beside the undecided order, `Expected: 1
Actual: 2`; (e) "the book ES -1", `Expected: 0 Actual: 1`. Guard (the flatten over the agent's WORKING close → one close) green before and after. Mutant: item 1's call
replaced by a Sends verdict ⇒ (b) red, `Expected: 0 Actual: -2`; restored, green. (e) is simulated by `RecordingConnector.PositionsTrail` (inert unless set): a frozen
positions list that both the read and the close's own sizing answer, as ATAS's `ClosePosition` sizes from its position object. Builder's gate: Release 0 warnings; Unit
1466, Fault 470, 0 failed; `PressCloseOnceTests` + `InFlightSettleTests` + `SecondPressRefusedTests` 3×, 17 passed each.
**Manager's gate** at `bfd02045` (carried to `698e63d7`, only docs moved), Release: build `--no-incremental` 0 Warning(s), 0 Error(s); Unit 1485/1485 (9 m 10 s); Fault
471/471 (2 m 6 s); Integration 736/737, 1 skipped (11 m 17 s) → 0 failed. Names vs `main`: 2321 → 2327, 0 removed, 6 added ([Fact]/[Theory] 2273 → 2279). Scan clean; no trailers.
**CI:** branch run 37696688736 at `9d5ea94f` (the code tip; later commits docs only): ubuntu ✓ 13 m, macOS ✓ 24 m, windows ✓ 54 m, package ✓ 3 m; item 1 alone, 37694859394
at `c1a31a56`: all four ✓. Tests box: NOT RUN — "the machine does not answer" (22:33Z). Landing CI on `main`: a waiter is armed.
**OWED (NOT claimed in CONTRACTS):** the (c) residual — another press's in-flight row the owner confirmed "still working" on the card, whose platform update is then lost,
holds that Close all leg until the platform reports it: the card offers only "it is working" for such a row and nothing settles a press's row. A liveness gap, not a double
close; owed BEFORE LIVE as seat P's light `U-press-row-answer` (a two-press "it is not working — close over it" answer on the card, the orchestrator's view).
**NOT verified:** where ATAS cannot prove its history every in-flight row is (d), never (b) — read, not run; whether ATAS lists a cancelled close at once after the press's
cancel (no ATAS box; a slow list refuses the leg, named); a lost `press_told` write is not exercised by a test; no app run, the words not seen on screen.

## 2026-10-08 — U-bar-holdout landed: no caller on the agent-facing pipe reads a bar from inside any holdout window, of any pair, through another dataset or the forward door — refused in words, never clipped — and the referee still does

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-bar-holdout.md` (briefed from seat A's read-only survey on the orchestrator's urgent ruling
of 2026-10-07 23:52; the survey's probe `SBarHoldoutProbe` was RED on `d73ecd59`: a second dataset of the pair served the Research Director and a roleless caller 60
held-back bars, a `backtest --dataset B` over A's window answered metrics and 5 trades charged to no campaign, and `data-bars --source forward` served the same minutes;
dispatched `1891074e`). The builder rebased once onto `7e29fc65`, no conflict. Merge `ea88e72b` (ff-only), 5 commits (3 items, a CI fix, the report), 21 files, +1097/−113.
**No schema change.** EVIDENCE: the holdout (`docs/COUNCIL.md` "holdout data the research process cannot reach", "a leaked holdout cannot become unseen").
**The rule (seat A's decision at dispatch, on the orchestrator's view):** EVERY subject, as the tape since U-tape-holdout — a cutoff on any dataset holds every pair's bars
over its window `[holdout_from, last bar + one bar)`, from every dataset and the forward bars; a dataset's own cutoff keeps `Holdout.Refusal`, asked first, unchanged.

- **Item 1 (`03c310fe`):** `DatasetReader.Read` and `BarFeed.Open` take the `TapeHoldout` with its ledger as a REQUIRED argument, ask `Holdout.Refusal` first, then refuse
  any OTHER dataset's window reached by the read's span `[from, to + one bar)`; `Backtest.Over` builds ONE `TapeHoldout.Of` for bars and features; `data-bars` and the referee's feed pass theirs; a refusal answers `HOLDOUT_WITHHELD`.
- **Item 2 (`a40d7369`):** the forward door — `ForwardBarStore.Window(holdout, symbol, from, to, cap)` is the pipe's read; `ForwardBars_` calls it; `Since` and `Bar`
  say they are never a pipe read (the paper runner's and `ForwardBarSource.Announce`'s).
- **Item 3 (`1e8c81b6`):** CONTRACTS "The holdout" states the rule; "Forward bars" and the runner's paragraph drop the post-dates-every-freeze premise, the latter
  stating as a KNOWN GAP that a paper run's figures over a later-held window are not held back; `GatewaySchema` (`market_data`, `data-list`, `data-bars`, `backtest` and its `to`) and `WorkspaceBuilder`'s history paragraph tell agents.
- **CI fix (`6e126d9d`):** `FeatureProgramBacktestTests.A_backtest_whose_features_reach_a_holdout_window_is_refused`'s other-dataset leg asserted the old rule (CI
  37714038502 red on all three jobs in that one test); moved to the new one.
- **Answer first:** no in-process bar read needs a read without the holdout — every one is the referee's (`TapeHoldout.Of` under `BarAudience.Referee`, passing every
  window) or the forward runner's (`ForwardBarStore.Since`/`Bar`); `DatasetReader.Read` has one caller, `data-bars`.
- **Declared deviations, accepted:** (1) no rename — `TapeHoldout` gains `Refusal(DatasetRecord, …)` and `ForwardRefusal`, `TapeHoldoutWindow` gains `Overlaps` and
  `BarWords`; (2) the forward read bounds `from` inclusively in SQL (the old door read from one bar before it); (3) test (f) names FOUR reads without a holdout —
  `ForwardBarStore.Since`, `ForwardBarStore.Bar` (public for `Announce`), a feed's `Bars`/`Chunks` — a feed streams only the window it was opened over (a wider stream
  throws, tested), and an IL read of `GatewayPipeServer` holds that it never calls `Since`/`Bar`.
- **Rewritten under this protection, JUDGED (seat A):** `ForwardBarsOverPipeTests.Data_bars_serves_forward_bars_to_a_role_bounded_and_without_a_holdout_refusal` —
  it asserted the leak as the feature; the name is kept and stays true (a window outside every holdout is served), the window reaching the archive's holdout is now refused through both doors. No test deleted or renamed.
**Verified by running (the builder, quoted; then the manager's gate):** builder: Release `--no-incremental` 0 warnings, 0 errors; Unit `Passed 1487, Failed 0` (6e126d9d);
Fault `Passed 471, Failed 0` (1e8c81b6, same src); Integration `Passed 741, Skipped 1, Failed 0`; 3× unit BarHoldoutTests, FeatureProgramBacktestTests, BarFeedTests,
CampaignLedgerTests, CandleSourceTests, DatasetLedgerTests 57/57 and integration BarHoldoutOverPipeTests, ForwardBarsOverPipeTests, MidpointEvidenceTests 18/18 each run.
RED before (base `1891074e` + the new tests): (a) "VERDICT: LEAK in 14 legs" ("data-bars BTCUSDT [2026-08-01T01:00:00Z, 2026-08-01T01:59:00Z] as the Research Director
over the pipe: SERVED 60 bars from dataset 2"); (b) "VERDICT: LEAK in 18 legs" (the forward door); (c)(d)(e) red on the base. Mutant (the cross-dataset check in
`BarFeed.Open` removed) ⇒ (a) "VERDICT: LEAK in 2 legs" (`backtest --dataset 2` for both directors); restored.
Manager's gate at `ea88e72b`, Release: build `--no-incremental` `0 Warning(s)`, `0 Error(s)`; Unit 1487/1487 (9 m 9 s), Fault 471/471 (2 m), Integration 741/742, 1 skipped
(11 m 15 s) → 0 failed. Names vs `main`: 2327 → 2334, 0 removed, 7 added. Scan: one hit, JUDGED a false positive (seat A) — `Grants.Issue(role, "attempt-bar-holdout").Token`,
a test's in-process role grant read at run time, no literal value (the pattern of `ForwardBarsOverPipeTests.cs:48`) — excluded by name at check and record; no trailers; `rev-list` 0 both ways.
**CI:** run 37717270395 at `6e126d9d` (the code of the merge; only the report follows): success on all four jobs (windows 57 m, ubuntu 13, macos 16, package 4).
**Tests box:** NOT RUN — 03:40 `ready : NO - his own OFT.Platform is open; nothing of ours runs beside it` (exit 5), not retried.
**NOT done, NOT verified (→ seat A's U-holdout-campaign):** a backtest over a version with no cutoff is charged to no campaign; the owner's Settings line after a second
Download says nothing is held while the first campaign is open; `SetHoldout` over months already served; a paper run's figures over a later-held window are not held back.
Tests (a) and (b) cover the pipe AND the in-process surface (`CallAsync`). Whether attempt 3's agent read held minutes is unknown (its record claims no verdict).

## 2026-10-08 — U-agent-tree landed: Pause, Stop, a turn's end and the app's quit, update or death end the turn's whole process tree on macOS and Linux before its spend row closes, its presence ends and its grant lapses, killing only what is proved the turn's

One fresh Opus builder under seat P built it from `docs/briefs/U-agent-tree.md` (seat A's read-only survey `s-agent-tree`, measured on this Mac at `cc7a0974`; re-checked by seat P
at `8319b390`, queued `7efb5f7d`, dispatched `6bd92855`; the orchestrator's protection ruling of 23:18). Merge `e7129357` (ff-only), 9 commits (tests `146378e6`, items 2–5 `7473ca35` `08c2c751` `d44489ef` `00ca678a`, Windows arms `49c391fa`,
`d67d1c0e`, `f840042c`, report `e7129357`), rebased by seat P's prep onto `28a2e3fb` then `cbffcc8b` with an identical src+tests patch-id. No rung, no schema, wire or verb change. PROTECTION: operator authority (Pause means the AI is stopped) and the owner's
spend (the ledger measures every paid process or refuses the next turn; nothing a turn spawned reaches the pipe after its turn).
- **Item 1:** `AgentTreeTests`, 17 tests — (a) a paused turn with and without the launcher, a Research role's too; (b) a stopped turn; (c) a finished turn's leftover; (d) children
  that left the group or the session; (e) the app's dispose mid-turn; (f) the app's death; (g) the row and presence only after the tree; (h) the guard — a process outside the
  tree with the turn's command survives; and the failed-teardown path. Windows arms in PowerShell (cmd parses the Situation, ResumeOnStartTests' reason).
- **Item 2:** `TreeTeardown`/`ProcessTable` (Core; libproc on macOS, `/proc` on Linux, no `ps`): freeze from the leader down parent links and across the session, re-prove pid +
  start time after SIGSTOP (else SIGCONT, left alone), SIGKILL, repeat ≤ 3 s; never the app's own session or group. A failed teardown revokes the grant at once and `TurnMeter`
  refuses every launch, naming the pids, until they end. Declared deviation, ACCEPTED: the session id is remembered at the start (the leader's pid by construction), not at
  `Held`'s latch, so a leader that exits unobserved still leaves its session findable.
- **Item 3:** Pause cancels the chair's conversation and every conversation the loop took a turn on before it cancels the loop (kept as `ConversationFor` gave them — asking the
  host at Pause would race its lazy dictionary); presence ends after the teardown, never on the leader's exit; `Run`'s timeout and the key sign-in take the same order.
- **Item 4:** `AppHost.DisposeAsync` pauses (`Mission.PauseAsync`; the resume choice asserted kept) and stops the AI first, once; Quit holds `ShutdownRequested` ≤ 15 s.
  **FINDING, from Avalonia 12.1.1's IL:** `Shutdown()` passes force and raises NO `ShutdownRequested`, so the app's SELF-UPDATE path never ran the old handler — before this
  unit an update never stopped the AI nor disposed what that handler disposes; `Exit` now runs the same stop, and `MainWindow`'s comment (which claimed it did) is corrected.
- **Item 5:** `--spawn-contained` stays resident (setsid, `Process.Start` with stdio inherited, the exit code forwarded) and sweeps its session when the command exits or
  `getppid()` changes: (f), the parent SIGKILLed, leaves every process dead within 5 s on macOS and ubuntu. The launcher still grants nothing and opens no pipe.
- **Extra commits, ACCEPTED (inside the brief's "a turn's end" path):** `d67d1c0e` ends the tree the moment the leader exits — CI 37707921636 measured a finished turn whose
  leftover held stderr and never committed; `49c391fa` adds the Windows arms. **Judged: a PROTECTION class in `Timing`** (`f840042c`): its verdict is a 5 s ceiling over real
  processes, argued at the class with measured numbers (37712519465 under load: an in-memory test 9.4 s against 0.1 s here; three PowerShell turns not up within 60 s at
  01:49–01:52Z, the same probe up in 7 s from 01:53Z); no timeout raised, no assertion changed. Timing grants a second attempt, so the FIRST-ATTEMPT results are recorded
  here: 37717264247, all three platforms passed on the first attempt (Unit Timing 18/18) — a later run that passes only on its retry is to be read as a sighting.
**Verified by running (the builder, quoted).** RED before (`6bd92855` + the item-1 tests, this Mac): 10 of 10 red — "5 s after Mission.PauseAsync, the launcher deployed, the
turn's tree still ran: ownpgrp (pid 68379), ownsess (pid 68380)"; (g) "presence.Live read 0 while leader (pid 68448), ownpgrp (pid 68452), ownsess (pid 68453) still ran; the
turn's ai_attempt row read ENDED while …". Mutant `Dispose` → `if (!Process.HasExited) End();`: at item 2 "5 s after the turn's own end (exit 0), the turn's tree still ran:
leftover (pid 69508)"; at item 5 "… after the session's leader had exited, the turn's tree still ran: leftover (pid 74365)"; reverted. Builder: Release 0 warnings; Unit 1502,
Fault 471, 0 failed; `AgentTreeTests` 3× 17/17; no probe process left on the Mac.
**Manager's gate** at `6f3baf5f` (carried to `e7129357`, only docs moved), Release: build `--no-incremental` 0 Warning(s), 0 Error(s); Unit 1504/1504 (9 m 28 s);
Fault 471/471 (2 m); Integration 741/742, 1 skipped (11 m 15 s) → 0 failed. Names vs `main`: 2334 → 2351, 0 removed, 17 added ([Fact]/[Theory] 2286 → 2303). Scan: 12 hits,
all "token" in identifiers and comments (`launch.Token`, `bound.Token`, "the loop's token"), no value: judged false positives, excluded by name. No trailers.
**CI:** branch run 37717264247 at `81ed13ce` (the code tip; the report docs only): macOS ✓, ubuntu ✓, windows ✓, package ✓. Earlier runs on the branch, read: 37694897915 red
only on items 3–5 not yet built; 37712519465 and 37707921636 windows red on the new PowerShell arms before `2d35f6d9`/`81ed13ce`; 37707921636 ubuntu red in
`BridgeRoundTripTests.A_live_refusal_is_not_masked_by_a_stale_one` (TimeoutException :813, the stub bridge's pipe; unreachable by this diff, green on main 37706652101 and on
every later run) — a FIRST SIGHTING. Tests box: NOT RUN — "his own OFT.Platform is open" (02:28, 03:21). Landing CI on `main`: a waiter is armed.
**NOT verified / owed:** (f) on Windows and Windows' start gap → `U-contain-seats`; a process that leaves the session AND loses its parent before a teardown cannot be proved
the turn's and is left (the honest limit); on Unix without the launcher a leftover holding the turn's pipes holds the turn open; no app launched and no real codex turn — the
quit hold, the update path's stop and the supervisor are proven in tests only.

## 2026-10-08 — U-size-cap landed: a risk-sized entry may declare the most of its capital it spends, so a tight stop sizes to the cap instead of to no trade; the shipped opening-range breakout gains a 0.95 cap

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-size-cap.md` (briefed by seat A's M0-gaps survey of 2026-10-07 23:51 — attempt 3's Research
reported "55 signals unfunded" and that `risk_fraction` "has no cash-notional cap", true of the product; re-checked at `78be3e9d` after U-language-v2a and dispatched
`7a053887`, the shipped-breakout fold seat A's call). The builder rebased onto `7e29fc65`; the manager's prep onto `c05b5d14` (over U-bar-holdout and U-agent-tree), then, docs only, onto `055bd53d`; src+tests patch-id identical
(`b454be962c6d`). Merge `ccfcca96` (ff-only), 5 commits (4 items, the report), 20 files, +972/−60.
**No schema change.** Protects: total parse and freezing by hash (every v1 and v2a text keeps its canonical form, id and trace — the one DECLARED exception item 4's
shipped file); leverage unspellable; every gate untouched — a cap only ever makes a size smaller; `StrategyVersions.Manifest`, `LanguageVersion`, `EvaluatorVersion` unmoved.

- **Item 1 (`7a38c50a`):** `size risk_fraction <f> max_capital_fraction <c>` (a NUMBER or a declared constant) sets `Sizing.MaxCapitalFraction`; refused on the size line
  in words — a cap on `fixed` or `capital_fraction`, ≤ 0, > 1 (the leverage words), no value, any other tail; never reserved; the canonical size form gains
  ` max_capital_fraction:<c>` ONLY when declared; `StrategyDeclarations.MaxCapitalFraction` in `All` and in `Of` when declared; both readers say they apply it.
- **Item 2 (`8a6fc4f2`):** `Quantity` = min(`equity * f / (reference − stop)`, `capital_fraction`'s own `OfCapital`) — the backtest's cash, a paper run's allocation
  ceiling; capped 4.75 = 1,000 · 0.95 / 200 sent and filled at minute 3's open 202; uncapped 6.25 refused ALLOCATION_EXCEEDED, nothing at the wire.
- **Item 3 (`7ce17e8c`):** STRATEGY-LANGUAGE (grammar, Sizing, the cap's arithmetic — applied at the close, fees, slippage and the next open on top — every limit
  refusing whole), CONTRACTS, `GatewaySchema`'s `capital`. Backtested: cap 1 at an open equal to the close "cannot pay", fills at 99.88; 0.95 fills at 100 and 105.
- **Item 4 (`f5388e62`), seat A's call:** the shipped `opening-range-breakout.strategy` (risk 0.01, `stop atr 2 14`, 1m — its risk size `0.01 · close / (2 · ATR14)` of
  equity exceeds all of it whenever a minute's ATR is under 0.5 % of the price) and its doc copy say `size risk_fraction riskfraction max_capital_fraction 0.95`
  (0.95, not 1, by item 3's arithmetic). **DECLARED RE-PIN, JUDGED (seat A):** its id `70ec1a6e45dc4509…` → `5ac50a1e374b6345…`; the old text is kept as
  `DayOnePrograms.BreakoutV1` (SHA-256 `ca509cf4…` = the old file's, id still `70ec1a6e…`), which `DeclaredBarsGuardTests`/`FeatureProgramGrammarTests` now read.
- **Declared deviations, accepted:** (1) `FeatureProgramRequirementTests` (l) compares the parser's unknown-word list with `All` minus the clause (it is no line's first
  word), asserts the clause refused as a line and read on the size line, and `EveryKind` sizes with the cap so `Requires == All` holds; (2) `Sizing`'s cap defaults to
  null, so every existing construction stands; (3) `StrategyCanonical.Size` fails CLOSED on a cap on a non-risk kind (unreachable from the parser).
- **Test edits, JUDGED:** `DeclaredBarsGuardTests`/`FeatureProgramGrammarTests` read the v1 fixture (`ShippedIds` → `V1Ids`); `DayOneStrategyTests` pins the new id,
  canonical form and `Sizing` and adds the v1-fixture test; `ForwardRunnerTests.ReadyAsync` takes optional envelope ceilings (defaults unchanged). None removed,
  renamed or weakened; `EvaluationGoldenVectorTests` and `FeatureProgramGoldenVectorTests` unedited and green.

**Verified by running (the builder, quoted; then the manager's gate):** builder at `920a6e38` (the code of `f5388e62` before the two rebases): Release `--no-incremental` 0 warnings, 0 errors; Unit `Passed: 1513, Failed: 0`;
Fault `Passed: 471, Failed: 0`; 3× SizeCapTests 27, FeatureProgramRequirementTests 2, DeclaredBarsGuardTests 2, FeatureProgramGrammarTests 5, DayOneStrategyTests 11,
Integration ForwardRunnerTests 26, green each run. RED before item 2: (a) `Expected: 76.00 Actual: 500`; (b) `Expected: 49.9251… Actual: 496.4061…`; (e) "would be
holding 1250 USDT of BTCUSDT and the capital allocated to it is 1000 USDT". Mutant 1 (the `min` dropped) ⇒ (a), (b), (e), (f) `Expected: 94.7158… Actual: 241.3793…`
and the four 0.12 % cases red; mutant 2 (the canonical form writing the cap undeclared) ⇒ (c) red, `Expected: "70ec1a6e45dc…" Actual: "c25660db4080…"`. Restored.
Manager's gate at `982e4525` (carried to `ccfcca96`: build tree identical), Release: build `--no-incremental` `0 Warning(s)`, `0 Error(s)`; Unit 1532/1532 (9 m 28 s), Fault 471/471
(2 m 1 s), Integration 742/743, 1 skipped (11 m 18 s) → 0 failed. Names vs `main`: 2351 → 2363, 0 removed, 12 added. Scan: `SCAN CLEAN`; no trailers; `rev-list` 0 both ways.
**CI:** run 37714812389 at `920a6e38`: success on all four jobs (windows 67 m, macos 16, ubuntu 12, package 4).
**Tests box:** NOT RUN — 02:13Z `ready : NO - his own OFT.Platform is open; nothing of ours runs beside it` (exit 5), not retried.
**NOT done, NOT verified:** that the shipped breakout never traded is arithmetic plus the (f) fixture (a 0.2 %-ATR New York morning: the old text "cannot pay" on every
signal, the new fills 94.715 = RoundDown(10,000 · 0.95 / 100.3)) — not run over a real dataset's minutes; no app run.

## 2026-10-08 — U-valuation-close-confirm landed: every lost close the app sends — the loss flatten, its again, the data-loss exit and the exit's new again — is confirmed from the platform's history per close generation, never once per breach

One fresh Opus builder under seat P built it from `docs/briefs/U-valuation-close-confirm.md` (queued `13aa9e48`; re-briefed by seat P's read-only survey at `78be3e9d`, its probes
at `d73ecd59` reproducing all three defects; committed `c0097bee`, dispatched `da4b06a6`). Owed BEFORE ANY LIVE USE (`U-flatten-confirm`; the again-flatten's owner-answered lost
close owed by `U-loss-hold-release`, folded in as one class fix). Merge `833dade5` (ff-only), 4 commits (item 1 `94f23e43`, item 2 `3f3ab893`, item 3 `870c67a7`, report `833dade5`), rebased by
seat P's prep onto `f9212bbd` then `e9234372` with an identical src+tests patch-id. No rung (kv JSON only). MONEY PATH (gateway): a lost close is never re-sent blind (`CLAUDE.md` rule 3); a position nobody can value
must not stay open because one close lost its answer.

- **Item 1:** a `CloseGeneration` record; `LostCloses` and `ApplyTheConfirm` work per generation, ONLY on that generation's own nonces; `loss_flatten_again_confirm:` written once,
  before any row is settled; only the first flatten's not-flat confirm owes a closing again, the again's own confirm owes nothing ("does NOT close it a third time … yours to
  close"); `LatestFlattenWord` reads the again's confirm first.
- **Item 2:** every `loss_valuation_exit:` of this connector, mode and account, and its again, is confirmed by the same routine (`loss_valuation_exit_confirm:`,
  `loss_valuation_exit_again_confirm:`): rows unflagged, the pause lifts, later exits go out. `CloseAgainWhatAnExitLeftOpenAsync` closes again ONCE (`loss_valuation_exit_again:`)
  only behind a not-flat confirm, no again written yet, the episode standing on THIS tick with `ExitKey` = that exit, the bound on and the episode at least that old, the
  connection up; the episode keeps its `ExitKey`; valued again → nothing sent, said. **The episode check is the one thing between this design and a THIRD close** (the mutant).
- **Item 3:** `ValuationReading` shows each exit's latest word (`LatestExitWord`: the again's confirm, the again "CLOSED AGAIN", the confirm as it stands now, the exit);
  `docs/CONTRACTS.md` (three notes and a section), `docs/USER-GUIDE.md`, the status field's doc, the schema's `loss_flatten` text, `AGENTS.md`.
- **Declared deviations, ACCEPTED by seat P:** (1) the again's not-flat confirm holds its closure on the book the tick reads, not on its record — else a position the owner
  closed by hand stayed held for ever; (f) asserts a hand close lifts it; (2) the exit's again checks its episode against the tick's own standing list
  (`TheEpisodeItWasSentFor`), not a disk row a failed write may leave stale; (3) the reading also shows an older exit whose latest word is today's, and says "NOT closing it
  again" once the reason has gone ((c)'s second arm); (4) a test knob, `RecordingConnector.QuoteAgeOf`. **Judged change:** `LossFlattenConfirmTests.A_lost_answer_to_the_
  closing_again_stays_for_the_owner` (name kept) asserted the defect; its claim moved to the history-hidden, no-answer case; every original assertion kept (the brief named it).

**Verified by running (the builder, quoted).** RED before, the base product: (a) `Expected: FILLED Actual: UNKNOWN`, "closes on the wire : 1; ES 0 NQ 1"; (b), (c) `Expected:
CANCELLED Actual: UNKNOWN`; (d) no confirm written (`Sub-string not found`), closes 1; (e) both arms `Expected: 1 Actual: 0` again's confirms; (f) `Expected: 1 Actual: 0`; (c)'s
second arm came with item 3 (no red-before run). Mutant: `TheEpisodeItWasSentFor` replaced by the ES episode row read from disk unchecked → (c) `Expected: 0 Actual: 1` again
records, "closes on the wire : 3; ES 0 NQ 0" — a third close — on item 2's code and on both arms of the final code; the other six green; restored (sha256 matches, 33/33).
Unchanged vs main, sha256 per function: `AskTheHistoryAsync`, `TheOwnersAnswerAsync`, `SettledByTheOwner`, `CloseCapturedAsync`, the in-flight sweep, `AccountForTheFlattenAsync`
(its whole-kind unflag neither widened nor copied — seat P's Must NOT held), the precautionary cancel, the bound, `TrackValuations`. Builder: Release 0 warnings; Unit 1485,
Fault 479, 0 failed; the four touched classes 33/33 ×3.
**Manager's gate** at `bef297db` (carried to `833dade5`, only docs moved), Release: build `--no-incremental` 0 Warning(s), 0 Error(s); Unit 1532/1532 (9 m 29 s);
Fault 479/479 (2 m 2 s); Integration 742/743, 1 skipped (11 m 17 s) → 0 failed. Names vs `main`: 2363 → 2369, 0 removed, 6 added ([Fact]/[Theory] 2315 → 2321). Scan clean; no trailers.
**CI:** branch run 37714909967 at `9d6954ec` (the code tip; the report docs only): ubuntu ✓ 12 m, macOS ✓ 24 m, windows ✓ 62 m, package ✓ 4 m; 37714046749 at item 2's
`6ef5ff4e`: all four ✓. Tests box: NOT RUN — "his own OFT.Platform is open" (01:51Z). Landing CI on `main`: a waiter is armed.
**OWED (light, seat P):** a closing again that ends not flat WITHOUT a lost close (an order that would not cancel, a leg refused at the wire) still holds its closure on its
record; no test kills a pass between writing a confirm and settling its rows (the write-once-before-settle order is the design, not exercised by a crash test).
**NOT verified:** no ATAS (no box), no app run, the words not seen on screen.

## 2026-10-08 — U-run-trace landed: a read-only verb serves every closed trade of a completed research run, in bounded pages, to any role — never the referee's holdout run, never a run any holdout window now reaches

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-run-trace.md` (M0 gap (a): attempt 3's Research had "no retained per-trade rows or read-only command"
to attribute two runs' exits — trades 21+ of its three runs were served to no one; briefed by seat A's M0-gaps survey of 2026-10-07 23:51, re-checked at `72bcd8dd` over
U-bar-holdout's holdout object, dispatched `28a2e3fb`). The builder rebased three times, last onto `9b08a077`, no conflict. Merge `006696c2` (ff-only), 4 commits (3 items, the report),
16 files, +1615/−31. **No schema change.** EVIDENCE: the holdout and verdict-only evidence (`EDGE-FACTORY.md` "Agents see verdict and reason class only"); no op writes a run or a trade.

- **First, the measurement (the brief asked for figures, not guesses):** a 1,000-trade page of attempt 3's shape (its RSI program, BTCUSDT's 0.01/0.00001 grid, 0.1 % fee and 0.02 %
  slippage, 1,281 trades over 180,000 one-minute decisions) is 230,459 bytes on the wire, 224–236 a trade — under the 262,144 cap, so the 1,000-row limit stops an ordinary page.
- **Item 1 (`d953ad4e`):** `StrategyStore.ReadTrades` → `RunTradesPage`, taking the caller's `TapeHoldout` with its ledger; refuses (a) a row of role `referee` or any promotion's
  `holdout_run_id` — no instant, figure or hash of it; (b) a run whose dataset, re-read from the holdout's own ledger, `Holdout.Refusal` or any other window now refuses; (c) a version
  reading features whose read window reaches a tape holdout (`Backtest.ClosesOf` + `FeatureFeed.Reaching`, the arithmetic `Backtest.Over` now calls); a gone dataset or an
  unreadable version is refused, never guessed. `TradesOf` stays the in-process reader.
- **Item 2 (`2c67603d`):** op `run-trades` (a READ), its dispatcher arm, drain row, schema entry, `TradeOps`, `trade run trades`, a CONTRACTS paragraph (not in the brief); an IL scan
  holds that the pipe server never calls `TradesOf`.
- **Item 3 (`bdd42f5b`):** the backtest schema text and note name the first 20, `trade_count` and `trade run trades`; `HOLDOUT_WITHHELD`'s owner text names prices, recorded context
  and trades (seat A's light fold).
- **Declared deviations, accepted:** (1) `TapeHoldout.Refusal(DatasetRecord, …)` does not ask the set's own cutoff, so 1(b) asks `Holdout.Refusal` first, as `DatasetReader` and
  `BarFeed` do; (2) test (e) cannot go red with 1(a) removed, because 1(b) refuses the referee's run too (no end; a cutoff never clears) — so (b) carries two clause-alone legs (a
  referee-marked row no promotion names, a research row a promotion names); (3) ordinals are `Backtest`'s, from 0 — no `after` = from the first; (4) `figures` use the backtest's
  metric names (`signals` = intents).
**Verified by running (the builder, quoted; then the manager's gate):** builder: Release `--no-incremental` 0 warnings, 0 errors; Unit `Passed 1539, Failed 0` and Fault `Passed 479,
Failed 0` at `e46dd19584` (the tip less one unit test); 3×: RunTradesTests 8/8 (tip), RunTradesTests + BarHoldoutTests 9/9, RunTradesOverPipeTests + HoldoutOverPipeTests 17/17.
RED before (guards off for pipe callers, `ed7df631`): (b) "3f17aa4eca81 as the Research Director over the pipe: the referee's holdout run was served"; (c) "the Operations Director
over the pipe was served a run whose window a cutoff set after it now reaches"; (e) 4/4 "'run-trades' served a bar at 2026-08-01 01:01:00Z, which is at or after the holdout cutoff
2026-08-01 01:00:00Z"; 1(c) "a run's trades were served though its feature reads now reach a holdout window". Mutants: the role clause dropped ⇒ (b) red ((e) green, deviation 2);
the dataset read once ⇒ (c) red; 1(a) dropped whole ⇒ (b) red; 1(c) removed ⇒ 1(c) red. All restored.
Manager's gate at `006696c2`, Release: build `--no-incremental` `0 Warning(s)`, `0 Error(s)`; Unit 1540/1540 (9 m 29 s), Fault 479/479 (2 m 2 s), Integration 747/748, 1 skipped
(11 m 20 s) → 0 failed. Names vs `main`: 2369 → 2381, 0 removed, 12 added. Scan: one hit, JUDGED a false positive (seat A) — `Grants.Issue(role, "attempt-run-trace").Token`, a
test's in-process role grant read at run time, no literal value (as at U-bar-holdout) — excluded by name at check and record; no trailers; `rev-list` 0 both ways.
**CI:** run 37742320644 at `bdd42f5b` (the code of the merge): success on all four jobs (windows 46 m, ubuntu 12, macos 16, package 4).
**Tests box:** NOT RUN — `ready` exit 1, "NO - the machine does not answer", not retried.
**NOT done, NOT verified:** CI's windows job is the only Windows evidence; no test drives a page over 262,144 bytes through the pipe (the byte bound is the reader's test); a
tape-reading run's trades over the pipe are tested at the reader, not the wire; no app run.

## 2026-10-08 — U-holdout-later landed: a held window never shrinks while a campaign judges on it — moving a cutoff LATER is refused in the owner's words, and the words that called it harmless are corrected

Built by one fresh Opus builder under build-fleet seat A from `docs/briefs/U-holdout-later.md` (the later-cutoff leak, MEASURED by seat A's U-holdout-campaign survey at `ea88e72b`:
the owner's press moved A's cutoff from 01:00 to 01:30, research was then served the 30 minutes in between, while the referee kept judging from 01:00 and its verdict recorded
01:00 as what was private; ruled urgent by the orchestrator; dispatched `82b3592c`). The builder's tip was on `9b08a077`; the manager's prep rebased it onto `b35653c2` and, docs only, onto `c4d2bdb3`; src+tests patch-id identical
(`032ace6bcdce`). Merge `fb6cac36` (ff-only), 3 commits (2 items, the report), 13 files, +642/−67.
**No schema change.** EVIDENCE: the holdout (`docs/COUNCIL.md` "a leaked holdout cannot become unseen") and the verdict's record of what was private (`strategy_verdict.holdout_from`).

- **Answer first:** no other writer — the cutoff has one write in `src/`, `DatasetStore.SetHoldout`'s UPDATE, whose one caller is `TradingGateway.SetHoldout`, whose one caller is
  `SettingsView.ApplyHoldout`, the owner's press; `Record`/`RecordNew` never write it; only tests call the store directly.
- **Item 1 (`6c8fa8ac`):** the check lives IN THE STORE, reading the campaign ledger inside its own write, so no caller can leave it out. A later date while the dataset has an open
  campaign → Ok=False, nothing written (cutoff, class, campaign); the owner reads: "Nothing was changed. Campaign 1 still judges strategies on every bar from 2026-08-01 01:00 UTC
  on, and moving the date to 2026-08-01 01:30 UTC would show the AI bars those judgements use. To hold back a different period, download a fresh copy of the history and hold months
  back on that." The same date → Ok, same campaign; earlier → refused as before, minus "Moving it later is allowed"; a renewed child is named the same way; with no open campaign,
  later stays allowed (nothing judges). The press's comment and the Settings card say the date never moves and name the fresh download.
- **Item 2 (`50035ea0`):** the words the right way round — CONTRACTS' holdout paragraph, USER-GUIDE "Holding months back" (every pair's bars, the forward bars and the tape; the
  date never moves), the `data-list` note (the every-dataset rule, no forward exemption — U-bar-holdout's leftover) and `ForwardBars.cs`'s header. **Declared extension, accepted:**
  two rung comments in `Database.cs` and `VerdictRow`'s ("a cutoff can later move later"), the same false premise. `GatewaySchema` untouched (U-size-cap's file; its text still true).
- **Rewritten under this protection, JUDGED (seat A):** `CampaignLedgerTests`' second press (same date Ok, later refused, cutoff unmoved); and the brief's disclosed RENAME
  `HoldoutLedgerTests.A_cutoff_may_be_moved_later_because_that_withholds_bars_nothing_has_read` → `A_cutoff_moves_later_only_while_no_campaign_judges_from_it` (its name stated
  the false premise; `names.sh` counts it as 1 removed, accepted on the brief's terms).
- **Process, JUDGED (seat A):** the builder pushed its docs-only report commit `1879d3cb` with a plain `git push` instead of `ci-dispatch.sh` (FLEET.md: a builder pushes only through
  it) — disclosed, its own branch only, scanned main..tip first; no code moved after the CI run (the scan's three hits on the branch — the test's
  grant read, the report quoting it, and the word naming the private network, already on main — judged false positives, excluded by name at check and record). Accepted; the next builders were told again.
**Verified by running (the builder, quoted; then the manager's gate):** builder at `6f2a8ee2` (the code of `50035ea0` before the rebases): Release `--no-incremental` 0 warnings, 0 errors; Unit `Passed: 1532, Failed: 0`;
Fault `Passed: 479, Failed: 0`; 3× `HoldoutLaterOverPipeTests` 5/5 and `HoldoutLedgerTests|CampaignLedgerTests` 37/37. RED before (base `82b3592c` + the new tests, 4 of 5 failed):
(a) "a later cutoff was written while campaign 1 judges from 2026-08-01 01:00:00Z: A's cutoff is now 2026-08-01 01:30:00Z", its log `L.2 … SERVED 30 bars` for the Research Director
and a caller with no role, over the pipe and in process; (b) `Found: "later is allowed"`; (c) `Assert.False() … Actual: True`; (d) `Found: "No holdout applies"`. Mutant (the later
refusal deleted) ⇒ (a) RED, `L.2 … SERVED 30 bars` ×4; restored.
Manager's gate at `35c6a7e4` (carried to `fb6cac36`: build tree identical), Release: build `--no-incremental` `0 Warning(s)`, `0 Error(s)`; Unit 1540/1540 (9 m 30 s), Fault 479/479
(2 m 1 s), Integration 752/753, 1 skipped (11 m 10 s) → 0 failed. Names vs `main`: 2381 → 2387, 1 removed = the disclosed rename above (JUDGED), 7 added. Scan clean with the
exclusions; no trailers; `rev-list` 0 both ways.
**CI:** run 37741041936 at `6f2a8ee2` (the code of the merge): success on all four jobs (ubuntu 13 m, macos 16, windows 52, package); pre-rebase `382fa9d7` green too (37738255440).
**Tests box:** NOT RUN — 09:03 `ready` exit 1, "the machine does not answer", not retried.
**NOT done, NOT verified:** the app was not launched, so the Settings card's new words were never seen on screen. OWED (→ seat A's U-holdout-campaign): the same date pressed on the
card's OTHER button still rewrites the evaluation class while a campaign judges (CONTRACTS states it as not covered). Integration ran in full on CI only.
