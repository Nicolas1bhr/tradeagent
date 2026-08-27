# tools — the harness that verifies this product

TradeAgent ships for Windows and is developed on macOS, so "does it work" has two different answers
and this directory holds the scripts that ask both. **They are committed on purpose**: a proof that
lives in someone's shell history is not a proof, and every one of these was written because a claim
in `BUILD-STATUS.md` needed evidence behind it.

Nothing here contains a credential. Every script reads `~/.tradeagent/win.env` if it exists, so the
machine is configured once and never passed on a command line:

```bash
mkdir -p ~/.tradeagent && chmod 600 ~/.tradeagent/win.env    # create it with these contents:
export TA_WIN_HOST=<hostname-or-tailscale-ip>                # reachable over your own network/VPN
export TA_WIN_NAME=<tailscale-machine-name>                  # optional, used for `tailscale ping`
export TA_WIN_USER=<windows-username>
export TA_WIN_PASSWORD=<windows-password>                    # or set up an SSH key instead
```

It lives outside the repository on purpose: a credential that cannot be reached by `git add` cannot
be committed by accident.

Prerequisites on the Mac: `sshpass` (only if you use a password rather than a key), and
`pyobjc-framework-Quartz` for the screenshot script.

## Start every Windows session here

```bash
tools/win-state.sh          # can I actually do work on that machine right now?
```

Three different situations all present as "it did not work", and telling them apart by hand costs a
session each time: the machine is asleep or off the VPN; it answers SSH but there is no live desktop;
or everything is available. ATAS is a GUI program — it cannot start, sign in or load the bridge
without a live desktop — so this refuses to let that be discovered halfway through a trading test.
Exit code 3 means console work only.

**It distinguishes an RDP desktop from the console one, and that distinction is load-bearing.**
Windows keeps a `LogonUI` running in the physical console session whenever that console sits at the
lock screen, which on a machine only ever reached over RDP is permanently. Asking "is any LogonUI
running" therefore answers yes forever and reports a perfectly live remote desktop as locked. The
first version of this script did exactly that, and reported a machine as unusable while someone was
signed into it and running ATAS.

The consequence for captures: **`win-shot.sh` cannot photograph an RDP desktop.** Its scheduled task
lands on the physical console, which is a different desktop, so it captures blank while a remote
session is perfectly alive. Screen captures need someone signed in at the console.

## The two loops

**Fast loop — run and look at the UI on macOS.** Avalonia runs here, so this is seconds per
iteration instead of minutes.

```bash
tools/mac-run.sh                    # launches the app against an isolated TRADEAGENT_HOME
tools/mac-shot.sh /tmp/ui.png       # captures ONLY the app window, not the whole desktop
```

**Slow loop — prove it on real Windows.**

```bash
tools/win-push.sh                   # syncs the working tree to the Windows machine
tools/win-run.sh 'cd C:\ta\repo && dotnet build TradeAgent.sln'
tools/win-ps.sh  < script.ps1       # runs PowerShell without four layers of quoting (see below)
tools/win-shot.sh /tmp/win.png      # console desktop only — see win-state.sh above
```

**Use `win-ps.sh` for anything longer than one word.** `win-run.sh 'powershell -Command "..."'` has
to survive zsh, then ssh's re-parse, then cmd.exe, then PowerShell — four layers with four different
escape rules. Anything containing a quote, a `$` or a backslash arrives mangled, and the symptom is
**empty output rather than an error**, which reads as "the machine did not answer". `win-ps.sh`
base64-encodes the script as UTF-16LE and passes it via `-EncodedCommand`, so it crosses all four
layers untouched. It also silences PowerShell's progress stream, which otherwise arrives as a CLIXML
blob on stderr and looks like corruption.

### Two traps that cost real time

- **macOS `tar` smuggles AppleDouble `._*` files into the archive**, and the C# compiler rejects them
  as binary. `win-push.sh` sets `COPYFILE_DISABLE=1`; do not remove it.
- **A GUI program started over SSH runs in a session with no desktop.** Screenshots come back black
  and clicks go nowhere. `win-shot.sh` goes through a scheduled task with `LogonType Interactive` so
  the program lands on the real desktop — and that desktop has to be **unlocked**, or captures are
  blank white.

## winagent/ — driving the Windows GUI without a person

`probe atas` answers the measurement question. This answers the other one: **who presses the buttons.**
The remaining steps of this project are GUI work inside ATAS, and a session that has to stop and wait
for somebody to click Add is not a session, it is a relay.

```bash
tools/win-agent.sh install      # register it; it then starts at every logon by itself
tools/win-agent.sh status       # alive? in which session? is anyone even logged on?

tools/win-ui.sh windows
tools/win-ui.sh tree --window ATAS --depth 6
tools/win-ui.sh find --window ATAS --query Strategies
tools/win-ui.sh invoke --ref e42
tools/win-ui.sh shot --window ATAS --out /tmp/atas.png
tools/win-ui.sh raw '{"op":"batch","items":[{"op":"front","window":"ATAS"},{"op":"shot"}]}'
```

### Why it is built this way

- **A resident agent, not a script per action.** The previous approach registered a scheduled task
  for every click: seconds of latency each, and no way to hold a reference to anything. The agent
  keeps element handles between calls, so `tree` then `invoke --ref e42` acts on the element you
  actually looked at rather than searching again and hoping the second search agrees with the first.
- **UI Automation, not pixels.** ATAS is WPF, so its controls have names, types and invoke patterns.
  "The button called Start" survives a moved window, a changed theme and a different resolution;
  a coordinate survives none of them. Coordinates are still there for custom-drawn chart surfaces,
  and the agent says in its result when it had to fall back to one.
- **Files, not sockets.** A local socket would be faster and would also put a Windows Defender
  Firewall prompt on screen — in front of automation that has nobody to answer it, on a product whose
  promise is at most one such prompt ever. Polling a directory prompts nothing.
- **It says when it cannot work.** `ping` reports `can_drive_ui`, and a capture that came back
  uniformly black or white is labelled as such — because that is what a session with no desktop
  produces, and it looks exactly like a broken application.

### The one thing it cannot do for itself

**Windows logon.** An interactive session requires the account password, and no automation can
conjure one. Until somebody is logged on there is no desktop, `win-agent.sh status` says
`logged on: NOBODY`, and every capture would be blank.

The fix is one command, run once, ever — after which the machine logs itself in at every boot and
the agent starts itself with it. Run it **yourself**, from your own shell, so the password is only
ever handled by you (the SSH session is elevated, so this works remotely):

```bash
ssh "$TA_WIN_USER@$TA_WIN_HOST" 'C:\ta\tools\autologon\Autologon64.exe <user> <computer> <password> /accepteula'
```

Sysinternals Autologon is staged at that path already, verified `Valid` and signed
`CN=Microsoft Corporation`. It stores the password as an LSA secret rather than in plaintext in the
registry, which is why it is preferred over the `AutoAdminLogon` registry keys. Reverse it any time
with `Autologon64.exe /d`.

Know what it trades: the machine will boot straight to a usable desktop, so physical access to it
becomes access to the account. On a dedicated test box that is usually the right trade — but it is
yours to make, which is why this file asks rather than does.

## probe/ — the harness behind the headline claims

`probe/` is a small console app that exercises the promises this product is built on, against the
real vendor releases, with no window anywhere. It is the evidence quoted in `BUILD-STATUS.md`. It is
deliberately **not** in `TradeAgent.sln`: it verifies the product, it is not part of it, and it must
never end up inside a packaged build.

Three verbs.

```bash
tools/win-push.sh
tools/win-run.sh 'cd C:\ta\repo\tools\probe && dotnet run -c Release -- install codex'
tools/win-run.sh 'cd C:\ta\repo\tools\probe && dotnet run -c Release -- chat codex'
tools/win-run.sh 'cd C:\ta\repo\tools\probe && dotnet run -c Release -- atas'
```

`install` downloads and unpacks the AI CLI from nothing and reports where it landed. `chat` sends one
message and **counts visible windows across the whole process tree before and after** — the number
that matters is that it does not change.

### probe atas — the answer to step 3

`atas` is the instrument for step 3 of `docs/RESUME-HERE.md`: *read `Describe()` on a live connection
and record what `SupportsClientOrderId` and `SupportsOrderHistory` actually report.* Those two
booleans decide whether this product may ever trade unattended, and until this verb existed the only
consumer of either was `TradingGateway`, internally — so the single most expensive, hardest-to-repeat
event in the project would have produced no record.

It hosts the bridge pipe, waits for the bridge inside ATAS to dial in, and prints, each on its own
labelled line: whether ATAS is installed, where, and whether it is running; whether the add-on is in
the **Strategies** folder (and a warning if it is sitting in Indicators, which is a different folder
that ATAS silently ignores); whether the pipe answered and what protocol version the bridge claimed;
the hello payload **as received**; the four capability flags and the derived
`ReconciliationProvable`; and what that combination means for autonomy.

It is read-only. It places no order, modifies none and cancels none.

```bash
tools/win-run.sh 'cd C:\ta\repo\tools\probe && dotnet run -c Release -- atas'
tools/win-run.sh 'cd C:\ta\repo\tools\probe && dotnet run -c Release -- atas --wait 180'
```

- `--wait <seconds>` — how long to wait for the bridge to dial in. Default 60. Give it longer if you
  are starting the strategy in ATAS by hand while it waits.
- `--wait-anyway` — skip the ATAS detection gate and wait for the pipe regardless. For driving the
  pipe with a stand-in bridge while working on the harness itself; it proves nothing about ATAS.

Exit codes, so it is safe to run unattended: **0** the bridge answered and the output is the record,
**1** could not reach the bridge, **2** bad arguments. A capability reading of `false` is a valid
answer and still exits 0 — recording `false` is the job.

**TradeAgent must not be running while this runs.** TradeAgent owns the bridge pipe for as long as it
is up, and the bridge would connect to it instead of to the probe.

### Two things `probe atas` cannot tell you, and says so

- **`SupportsClientOrderId` is `false` on a fresh session by design.** It only turns true after the
  bridge has read one of its own client order ids back off a real order. The protocol carries one
  boolean and no attempt counter, so *"not proven yet"* and *"the round trip failed"* are the same
  value on the wire. The verb narrows it by reading ATAS's live order book and saying which reading
  the evidence supports — and it labels that as **inferred**, not reported. To answer the round trip
  itself, place one order (paper first) and run the verb again.
- **`SupportsOrderHistory = true` does not say how far back.** It says an order cache was reachable.
  A request older than ATAS's own retention is refused outright rather than answered with a short
  list, which is the point: a partial history makes "this order does not exist" look provable when it
  is not.

If either boolean comes back `false`, the gateway refuses `LIVE_AUTONOMOUS` on that connector. That
is correct behaviour and **must not be "fixed" by hard-coding either value true.**
