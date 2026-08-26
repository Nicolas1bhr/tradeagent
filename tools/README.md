# tools — the harness that verifies this product

TradeAgent ships for Windows and is developed on macOS, so "does it work" has two different answers
and this directory holds the scripts that ask both. **They are committed on purpose**: a proof that
lives in someone's shell history is not a proof, and every one of these was written because a claim
in `BUILD-STATUS.md` needed evidence behind it.

Nothing here contains a credential. Configure with environment variables:

```bash
export TA_WIN_HOST=<hostname-or-ip-of-the-windows-machine>   # reachable over your own network/VPN
export TA_WIN_USER=<windows-username>
export TA_WIN_PASSWORD=<windows-password>                    # or set up an SSH key instead
```

Prerequisites on the Mac: `sshpass` (only if you use a password rather than a key), and
`pyobjc-framework-Quartz` for the screenshot script.

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
tools/win-shot.sh /tmp/win.png      # needs the Windows desktop UNLOCKED, or it captures blank
```

### Two traps that cost real time

- **macOS `tar` smuggles AppleDouble `._*` files into the archive**, and the C# compiler rejects them
  as binary. `win-push.sh` sets `COPYFILE_DISABLE=1`; do not remove it.
- **A GUI program started over SSH runs in a session with no desktop.** Screenshots come back black
  and clicks go nowhere. `win-shot.sh` goes through a scheduled task with `LogonType Interactive` so
  the program lands on the real desktop — and that desktop has to be **unlocked**, or captures are
  blank white.

## probe/ — the no-terminal proof

`probe/` is a small console app that exercises the two promises this product is built on, against
the real vendor releases, with no window anywhere. It is the evidence quoted in `BUILD-STATUS.md`.

```bash
tools/win-push.sh
tools/win-run.sh 'cd C:\ta\repo\tools\probe && dotnet run -c Release -- install codex'
tools/win-run.sh 'cd C:\ta\repo\tools\probe && dotnet run -c Release -- chat codex'
```

`install` downloads and unpacks the AI CLI from nothing and reports where it landed. `chat` sends one
message and **counts visible windows across the whole process tree before and after** — the number
that matters is that it does not change.
