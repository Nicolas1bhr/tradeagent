# U-cut-0.1.2-b — watch the installed 0.1.1 update itself to the published 0.1.2, on the box

Fresh builder on Opus, with the box GRANTED to you alone. The release exists: `gh release view v0.1.2` — cut by the
manager from the tree the box built (`TradeAgent-Setup-x64.exe`, 117,979,718 bytes, sha256
`672f28fa5f43cbe12d8786264fbb42cbbe8a10ecd2e7bb1df4da3371e72dab66`, equal on the box, on the Mac, in `SHA256SUMS.txt`
and in the GitHub asset digest — four of the five places; yours is the fifth). Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`,
`docs/DEPLOYMENT.md` §1–§5 and §8, `docs/USER-GUIDE.md` § "Updates" (however it is titled), `docs/RESUME-HERE.md` (box
facts, traps 24/35/40/42/43), `tools/README.md`, and the `U-box-precut`, `U-bridge-2` and `U-attest-precondition`
sections at the end of `BUILD-STATUS.md`. `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no
`timeout`. Worktree `~/Projects/ai-trading-software-for-mihael-worktrees/cut-0.1.2-b`, branch `u-cut-0.1.2-b` from
`main`, for the report only: this unit changes no file but the brief. Credentials are in `~/.tradeagent/win.env`; never
print them or a host name. Sim accounts only; never change mode, live activation, kill switch or settings; never close
ATAS unless capture works; the book stays flat.

**The box as the manager left it:** TradeAgent NOT running (the cut leg stopped it before its push and was killed
before restarting anything); `C:\ta\repo` holds the 0.1.2 tree and its `artifacts\`; the INSTALLED app is the released
0.1.1 (`%LOCALAPPDATA%\Programs\TradeAgent`, or wherever `docs/DEPLOYMENT.md` §2 says the per-user install lives); ATAS
is running with the bridge strategy at protocol 3 (deployed by `U-box-precut`); the UI agent may be down — start it.

1. **Start the INSTALLED 0.1.1 app** (never the repo build) and read `trade status` → `app_version 0.1.1`, the bridge
   row (the 0.1.1 app speaks protocol 2 and will REFUSE the protocol-3 bridge: quote that sentence — it is expected and
   is the reason the update must succeed without a trading connection).
2. **Watch the update.** Do nothing but read: the app checks the GitHub release feed, finds v0.1.2, verifies the
   installer against `SHA256SUMS.txt`, refuses to install over unconfirmed work if any, asks or proceeds as the guide
   says, runs Setup with Setup's own window only (no console — the product rule), and relaunches as 0.1.2. Quote every
   sentence the app showed and every activity line, with times. If it needs a press (the guide says which), press it
   through the UI agent, two-press if it is two-press. If it refuses, quote why and STOP: never install by hand.
3. **After:** `trade status` → `app_version 0.1.2`; the bridge row `connected · bridge 8.0.14, protocol 3` (if the row
   says the bridge must be reinstalled, press `Reinstall the bridge` on Checks and start the strategy: traps 24/40); every
   health row READY; the copy the app downloaded hashed → the fifth reading equals the four above; the installed
   `TradeAgent.exe`'s file version 0.1.2.
4. **Leave the box** with 0.1.2 installed and running, ATAS up with the bridge started, the book flat, the repo build
   stopped, the UI agent as you found it.

No code, no push, no release changes (do not delete or edit any release). Everything is a measurement, quoted.

## Report — append here, commit it on your branch, ≤20 lines: the sentences the 0.1.1 app showed before, during and
after the update, with times; the fifth hash; `trade status` after; the box state left; what you did NOT do.
