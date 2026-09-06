# U-cut-0.1.2-c — watch the installed 0.1.1 update itself to the published 0.1.2 from a FRESH home

Fresh builder on Opus, with the box GRANTED to you alone. `U-cut-0.1.2-b` reported NOT DONE (its report is on branch
`u-cut-0.1.2-b` @ `a23f540` and in the cut's section of `BUILD-STATUS.md`): the INSTALLED 0.1.1 app
(`%LOCALAPPDATA%\Programs\TradeAgent\TradeAgent.exe`, file version 0.1.1.0, schema 1) cannot start on the box's real home,
whose `state\tradeagent.db` is at schema 3 from this week's repo builds — it shows "TradeAgent cannot start /
TradeAgent's records are damaged." with no shell and no update strip, so it never reads the release feed. The
measurement moves to a fresh home. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/DEPLOYMENT.md` §1–§5, §8,
`docs/USER-GUIDE.md` (updates; the setup journey), `docs/RESUME-HERE.md` (box facts, traps 24/35/40/42/43),
`tools/README.md`, `src/TradeAgent.Core/Paths.cs:60` (`TRADEAGENT_HOME` overrides the root). `export
PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. Worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/cut-0.1.2-c`, branch `u-cut-0.1.2-c` from `main`, for the report
only. Credentials in `~/.tradeagent/win.env`; never print them or a host name. Never change the real home's mode, live
activation, kill switch or settings; never close ATAS unless capture works; the book stays flat; no push, no build.

1. **A fresh home.** Start the INSTALLED 0.1.1 with `TRADEAGENT_HOME=C:\ta\home-0.1.1-update` (a new, empty folder; the
   real home untouched — prove that by the real `state\tradeagent.db` mtime before and after). Quote what it shows.
2. **The update, if the app offers it before setup completes:** do nothing but read — it finds v0.1.2 on the release
   feed, verifies the installer against `SHA256SUMS.txt`, asks or proceeds as the guide says, runs Setup with Setup's own
   window only, relaunches as 0.1.2 on the same fresh home. Quote every sentence and activity line with times; press
   only what the guide says, through the UI agent. If it refuses, quote why and STOP.
3. **If setup must complete first:** walk the setup journey on the fresh home ONLY up to, and NOT including, any step
   that installs 0.1.1's bridge into ATAS (that would overwrite the protocol-3 bridge the box runs; if the journey cannot
   be passed without it, STOP there and say so) — then look again for the update.
4. **After the update:** the installed exe's file version 0.1.2; `trade status` against the fresh home (set the same
   `TRADEAGENT_HOME` for the CLI) → `app_version 0.1.2`; the copy the app downloaded, wherever the guide says the app
   keeps it, hashed — the FIFTH reading must equal `672f28fa5f43cbe12d8786264fbb42cbbe8a10ecd2e7bb1df4da3371e72dab66`.
   Then stop the app on the fresh home. Do NOT start 0.1.2 against the real home in this unit: that home is at schema 3
   and 0.1.2 carries schema 4 — the migration is the owner's moment, not a side effect of a measurement.
5. **Leave the box:** no TradeAgent running, ATAS up with the bridge started, the book flat, the fresh home left in
   place for the record, the UI agent as found.

## Report — append here, commit it on your branch, ≤20 lines: every sentence the app showed with times; whether the
update came before or after setup; the fifth hash and where the copy was; `trade status` after; the exe's version;
the real home's db mtime before and after; what you did NOT do.
