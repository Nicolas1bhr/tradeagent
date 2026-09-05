# Deploying TradeAgent to a machine

**Audience: Nicolas, sitting at the machine.** Not the owner. This file names commands and paths; the
owner's document, `docs/USER-GUIDE.md`, does not and must not.

**Every step below is marked.** *Walked* means a record shows it done at least once on real Windows
hardware, and names the record. *Not yet walked* means nobody has done it and the words are a plan.
Do not promote a step from the second category to the first without a record.

The two records this file leans on are `docs/hardening/records/U3-update-proof.md` (the release, the
install, the update) and `docs/hardening/records/U4-windows-eyes.md` (the setup journey, the override
card, the refusal sentence).

---

## 0. Preconditions

| | | |
|---|---|---|
| Windows 11 | `MinVersion=10.0.22000` in the installer refuses anything older (`packaging/TradeAgent.iss:62`) | walked (U3) |
| x64 | `ArchitecturesAllowed=x64compatible` (`packaging/TradeAgent.iss:58`) | walked (U3) |
| ATAS installed and signed in to a broker | TradeAgent never sees the broker credentials and never asks | walked (U4) |
| At least one account visible in ATAS | simulated is fine and is the right place to start — U4 used `CRYPTO5EB41`, a Binance crypto-sim account | walked (U4) |
| ~2 GB free | the doctor's "Free disk space" check fails below it (`src/TradeAgent.Diagnostics/Doctor.cs:58`) | walked (U4, as part of the journey) |

Nothing else. No .NET, no Node, no developer tools: the app and the `trade` CLI are published
self-contained (`packaging/build.ps1:110,117`).

**Do not install TradeAgent while ATAS is holding the Strategies folder open** if you intend to
deploy the bridge in the same sitting — see §4.

---

## 1. Get the release onto the machine

Two assets per release: `TradeAgent-Setup-x64.exe` and `SHA256SUMS.txt`. Both are produced by
`packaging/build.ps1` and the checksum file is not optional — the app refuses an update it cannot
verify against it (`docs/USER-GUIDE.md` § "When it refuses"; `src/TradeAgent.Provisioning/UpdateService.cs:543-571`).

Cut a release the way U3 did:

```powershell
gh release create v0.1.2 --target <FULL 40-char sha> artifacts\TradeAgent-Setup-x64.exe artifacts\SHA256SUMS.txt
```

**A short sha is rejected with HTTP 422.** `--target` takes a branch or a full sha, nothing else
(U3 § "Building and publishing v0.1.1"). *Walked.*

Before publishing, read the build's own summary block (`packaging/build.ps1:310-319`). The line that
matters is:

```
ATAS adapter      PRESENT - AtasStrategyAdapter is compiled into the bridge assembly
```

`ABSENT` means the build cannot trade through ATAS at all. It is read back out of the compiled
assembly, not from the switch that was passed in (`packaging/build.ps1:210`). *Walked (U3).*

Hash the installer on both machines and against the release asset before trusting it. U3 got the same
`sha256` and the same byte count in five places: the box, the Mac, `SHA256SUMS.txt`, the GitHub asset
digest, and the copy the app itself downloaded. *Walked.*

---

## 2. Run the installer

Download it in a browser on the target machine and double-click it.

**SmartScreen: not yet walked.** The installer is unsigned — code signing is deferred, deliberately —
so a browser-downloaded copy carries a mark-of-the-web and Windows should show *"Windows protected
your PC"*, where **Run anyway** is hidden behind **More info**. Nobody has seen this for this product:
U3's install came down without a `Zone.Identifier`, so no warning appeared (U3 § "Pre-state",
§ "NOT VERIFIED"). Walk it once and record what the screen actually says.

What *is* walked (U3): the installer runs with **Setup's own window only** — no console, no UAC
prompt, no error box. It is per-user (`PrivilegesRequired=lowest`, `packaging/TradeAgent.iss:47`), so
it installs into `%LOCALAPPDATA%\Programs\TradeAgent`, writes an HKCU uninstall entry, and touches
nothing outside the account. There is no all-users question on screen; an administrator who wants one
passes `/ALLUSERS` (`packaging/TradeAgent.iss:42-48`).

The silent form U3 used, for a machine you are driving remotely:

```
TradeAgent-Setup-x64.exe /SILENT /NORESTART /SUPPRESSMSGBOXES /relaunch=1 /LOG
```

`/relaunch=1` starts the installed app afterwards. *Walked (U3: pid 7552).*

**Machine-wide install and elevation: not yet walked.**

---

## 3. The setup journey

Start the app and hand the machine to whoever owns it, or walk it yourself. There are sixteen
screens (`src/TradeAgent.Core/Onboarding.cs:4-12`), listed by their on-screen titles in
`docs/USER-GUIDE.md` § "Setting it up". On U4's walk **eight were shown and eight self-verified and
flashed past inside the two-second poll** (`src/TradeAgent.App/OnboardingView.cs:75`). Which eight
depends on the machine: a screen appears only while its probe is still false
(`OnboardingView.cs:403-450`).

Nothing in the journey asked for a terminal, an administrator, or a credential. *Walked (U4).*

**Resume works:** closed at 9/16, relaunched, came back on 9/16 with nothing re-walked. *Walked (U4).*
Progress lives in the database, not in the view (`OnboardingView.cs:23`).

Two things to know before you walk it:

- **`Back` goes to the last *decision*, not the last screen.** The decision set is `WELCOME`,
  `AI_RUNTIME_SELECTED`, `TRADING_PLATFORM_SELECTED`, `AGENT_READY`, `SETUP_COMPLETE`
  (`OnboardingView.cs:324-329`), and going back clears every step after the target
  (`OnboardingView.cs:344-355`). `ACCOUNT_SELECTED` is a genuine choice and is *not* a Back target —
  U4 defect 16.
- **Screen 9, "Connecting to ATAS", does not surface the connector's refusal detail** — U4 defect 1,
  the highest-value one on that list. If it sits on "Waiting for ATAS to connect." forever, the reason
  is in `_host.Connector.StatusDetail` and the screen is not showing it to you. Read it from
  `trade status --json` or `tools/probe atas` instead of re-walking the five instructions.

**Eight auto-passed screens on a clean machine: not yet walked** — U4 ran in a scratch home on a
machine that already had everything.

---

## 4. Deploy the bridge, and read `proto=3`

The bridge is a .NET assembly that runs *inside* ATAS. The installer stages it at
`<app>\bridge` (`packaging/build.ps1:46`; the whole stage directory is copied by
`packaging/TradeAgent.iss:93`), and the setup screen
**"Installing the ATAS bridge"** copies every `TradeAgent.*` file from there into ATAS's strategies
folder — `%APPDATA%\ATAS\Strategies` — overwriting what is there
(`src/TradeAgent.Connectors.Atas/AtasInstallation.cs:42,181-197`). The one that matters is
`TradeAgent.AtasBridge.dll`; its presence is what `Detect().BridgeInstalled` answers on
(`AtasInstallation.cs:115`).

**ATAS must be closed for the copy**, and **ATAS does not watch that folder** — after the copy the
owner opens a chart, opens Strategies, presses the refresh button at the top of the list, chooses
**TradeAgent Bridge**, then **Add**, then **Start** (`OnboardingView.cs:939,948-967`).

**The protocol number is now 3** (`src/TradeAgent.Core/Versioning.cs:35`), and compatibility is exact
equality — not "at least" (`Versioning.cs:48`). So:

> **Redeploy the DLL before you update the app on any machine that already has a bridge.** A
> protocol-2 bridge against a protocol-3 build is refused by design, with
> *"bridge 0.1.1 speaks protocol 2, this build speaks 3 — reinstall the add-on from TradeAgent"*
> (`src/TradeAgent.Connectors.Atas/BridgeProtocol.cs:165-167`). That refusal is permanent until a
> compatible hello repairs it (`AtasConnector.cs:280-286`).

Read the number back before you believe anything:

```bash
tools/win-run.sh 'cd C:\ta\repo\tools\probe && dotnet run -c Release -- atas'
```

The one-line summary at the bottom is the thing to paste
(`tools/probe/Program.cs:950-959`):

```
atas=… bridge=… proto=3 | SupportsClientOrderId=… SupportsOrderHistory=… IsSimulated=… | ReconciliationProvable=… | autonomy=…
```

`proto=3` and `PROTOCOL VERDICT: MATCH` are the two readings that say the deployment is whole
(`tools/probe/Program.cs:602-610`). `probe atas` is read-only: it places, modifies and cancels
nothing.

**One trap, from U4:** `probe atas` rewrites the real home's `state\bridge.auth` `server_image` to its
own path. Relaunching the installed app puts it back. Do not leave a machine after a probe run
without starting the app once.

**`proto=3` has never been read on the box.** The bridge redeploy at protocol 3 is exactly what
`docs/RESUME-HERE.md` lists for the v0.1.2 cut. *Not yet walked.*

---

## 5. Confirm health from inside the app

The dashboard's System health column carries twelve rows, and they are the same twelve everywhere —
`TradeAgent`, `Agent runtime`, `Agent process`, `Workspace`, `Gateway`, `trade CLI`, `ATAS process`,
`ATAS bridge`, `Trading connection`, `Account`, `Market data`, `Execution capability`
(`src/TradeAgent.Core/Health.cs:25-29`).

Four of them gate trading outright: **Gateway, Trading connection, Account, Execution capability.**
Anything but READY on any of those and the gateway revokes execution rather than guessing
(`Health.cs:69-80`).

The reading U4 recorded after a good setup:

```
mode PAPER · ai_trading_stopped false · live_activated false · execution_available true
connector atas · connector_is_paper TRUE · account CRYPTO5EB41
Agent runtime READY "OpenAI Codex CLI 0.147.0"
```

*Walked (U4).* And U3, after the update: `app_version 0.1.1`, every ATAS row READY, `ATAS bridge
READY "connected · bridge 8.0.14, protocol 2"` — that last string is now expected to read
`protocol 3`.

Then press **Check everything** on the **Checks** page. It runs a named check per part
(`Doctor.cs:43-167`) and prints the ones that are not healthy with a `what to do:` line. Two to
recognise:

- **"Fully automatic trading"** warns on ATAS unless both `SupportsClientOrderId` and
  `SupportsOrderHistory` come back confirmed (`Doctor.cs:193-213`). That is a correct refusal, not a
  fault: `Real, fully automatic` is withheld and the other three modes work.
- **"Order confirmation"** warns whenever there is unconfirmed work, and trading stays paused until a
  person or the reconciler settles it (`Doctor.cs:155-167`).

**The Checks page prints repair text but has no repair buttons** (`DashboardView.cs:910-919`). So
`"Press Install bridge."` is a sentence with nothing behind it — see §7.

---

## 6. Remote access, for a machine you monitor rather than sit at

Tailscale plus Windows OpenSSH. The scripts in `tools/` already assume it, and read
`~/.tradeagent/win.env` — `TA_WIN_HOST`, `TA_WIN_NAME`, `TA_WIN_USER`, `TA_WIN_PASSWORD` — which lives
outside the repository so a credential cannot be `git add`ed by accident (`tools/README.md`).

```bash
tools/win-state.sh          # start every session here
```

It answers the three situations that all present as "it did not work": asleep or off the VPN;
answering SSH but with no live desktop; or fine. **Exit code 3 means console work only.** It
distinguishes an RDP desktop from the physical console one, and that distinction is load-bearing —
`LogonUI` sits in the console session forever on a machine only ever reached over RDP.

Consequences worth knowing before you plan a session:

- **ATAS is a GUI program.** It cannot start, sign in, or load the bridge without a live desktop.
- **`tools/win-shot.sh` cannot photograph an RDP desktop.** Its scheduled task lands on the physical
  console. Captures need someone signed in at the console.
- **Use `tools/win-ps.sh` for anything longer than one word.** Four quoting layers otherwise, and the
  symptom of getting it wrong is empty output rather than an error.
- **`Start-Process`-based detach does not survive the SSH session** (Windows OpenSSH kills the
  session's job object). Launch long builds with `Invoke-CimMethod Win32_Process Create` on a `.cmd`
  and poll the log. *Walked, painfully (U3 § "Deviations").*

All of the above is walked; it is how U3 and U4 were done.

**Unattended logon is yours to decide, and you run it yourself** so the password is only ever handled
by you (`tools/README.md` § "The one thing it cannot do for itself"). It makes physical access to the
box equal to account access. On a dedicated test machine that is usually the right trade.

---

## 7. The first hour

In order. Stop at the first one that does not read as described.

1. **`trade status`** over SSH. `app_version` is the version you just installed;
   `ai_trading_stopped false`; `execution_available true`; `open_requests 0`; unreconciled `0`.
   *Walked (U3, U4).* If `trade` says *"no access token found; is TradeAgent installed and running?"*,
   the app is not running — the CLI reads the token the app writes
   (`src/TradeAgent.TradeCli/PipeClient.cs:17-18`).
2. **`tools/probe atas`** → `proto=3`, `PROTOCOL VERDICT: MATCH`, and the four capability flags. Then
   **start the app once** to put `bridge.auth` back (§4).
3. **Checks → Check everything** in the app. Expect "Everything looks healthy." or only the
   "Fully automatic trading" warning. *Walked (U4).*
4. **Leave the mode in Practice.** It is the default (`src/TradeAgent.Core/Trading.cs:46`), and
   real-money trading is a separate switch that does not survive leaving a live mode
   (`TradingGateway.cs:256-262`).
5. **Walk one order, on the simulator or a simulated ATAS account**, all the way to a broker order id
   and then cancel it, and check the book from outside TradeAgent afterwards. That is what U4 did with
   `probe atas` → `ORDERS IN LIVE BOOK : 0`, `orders=0 strategyorders=0 mytrades=0 position=0`.
   *Walked (U4).*
6. **Press STOP AI TRADING and press it again.** One press each way
   (`DashboardView.cs:638-642`); the button reads **RESUME AI TRADING** while it is on.
7. **Create support package** on the Checks page, and open the zip. It should contain
   `activity.txt`, `environment.json`, `engineering.log`, whatever is in the home's `logs\` folder,
   and any `bridge-coid-witness.errors.log*` beside the bridge
   (`src/TradeAgent.Diagnostics/Doctor.cs:244-323`). *Not yet walked as a first-hour step; the
   collector's own behaviour is unit-tested but no record shows a zip opened on a deployed machine.*
8. **Note the database's modification time.** U4 used it as the proof that a scratch run touched
   nothing: `state\tradeagent.db`, unchanged across the whole walk.

---

## 8. Rollback

**There is no rollback button, and rolling back has never been tried** (U3 § "NOT VERIFIED":
"downgrade, rollback, an interrupted install"). What follows is the plan, not a walked procedure.

The plan is: **install the previous release over the top.** The installer is per-user and replaces the
program directory; it does not touch `%LOCALAPPDATA%\TradeAgent`, which is where everything that
matters lives — `state\tradeagent.db`, `workspace\`, `logs\`, `bridge\`, `updates\`
(`src/TradeAgent.Core/Paths.cs:9-30`). U3 watched a *forward* update leave that database intact:
`open_requests 1` unchanged across the replacement, and the Activity page still carried the 2026-08-28
order history. The uninstaller leaves the home in place too, deliberately.

Two things that make a backward step different from a forward one, and neither is tested:

- **The database schema version is 3** (`Versioning.cs:42`) and migration is forward-only and additive
  by design. An older build opening a schema-3 database is untested. **Take a copy of
  `state\tradeagent.db` before you roll back.**
- **The bridge protocol number moves with the app.** Rolling the app back to a protocol-2 build over a
  protocol-3 DLL produces the same refusal in the other direction. Redeploy the bridge from the
  release you rolled back to.

If an install is interrupted — also never tried — the honest first move is to reinstall the release
you intended, from a copy whose checksum you have checked by hand.

---

## 9. What this document cannot tell you, because nobody has done it

- SmartScreen on a browser-downloaded copy, and code signing (there is none).
- A machine-wide install, or elevation during TradeAgent's own install.
- An update while the AI is running; an interrupted install; a downgrade; a rollback.
- An install on a clean machine — every walk so far was on a box with developer tools on it.
- The Inbox drop/picker COPY path (needs a person at the keyboard).
- Anything with a real broker or real money.
- `proto=3` read on hardware.
- **There is no in-app way to reinstall the bridge after setup completes.** The setup surface is only
  shown while onboarding is incomplete (`src/TradeAgent.App/MainWindow.cs:183`), the only caller of
  `Onboarding.Clear` is the wizard's own Back (`OnboardingView.cs:349`), and neither Settings nor
  Checks offers the action. Today the repair is: close ATAS, copy `TradeAgent.*` from the installed
  app's `bridge\` folder into `%APPDATA%\ATAS\Strategies` by hand, reopen ATAS, refresh the strategy
  list, Add, Start. **That is a terminal-free product asking a person to move a file, and it is a
  defect, not a procedure.** It is in the user guide's "Still not finished" list.
