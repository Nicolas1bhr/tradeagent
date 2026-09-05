# U-box-precut — the v0.1.2 box session, everything except the cut, on the pushed tree at `67ae250` (code `d92a61b`)

Fresh builder on Opus, with the box GRANTED to you alone for this unit. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`,
`docs/RESUME-HERE.md` (the "Confirm the machine" block, the five facts, and traps 24/35/40/42/43), `tools/README.md`
(all of it: win-state, win-run, win-push, winagent, probe atas, atas-gate), `docs/DEPLOYMENT.md` §4–5, and the
U-bridge-reinstall and U-press-atomic sections of `BUILD-STATUS.md`. `export PATH="$HOME/.dotnet:$PATH"
DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`; no full suite on this Mac (another leg is running one). Worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/box-precut` on branch `u-box-precut` from `main`. Credentials are
in `~/.tradeagent/win.env`, sourced by the scripts: never print them, never quote a host name or IP into a file (write
`$TA_WIN_NAME`). ATAS's accounts are SIMULATED and no broker is attached: orders on them are yours to place; you never
change mode, live activation or the kill switch, and you never close ATAS unless capture works (trap 43).

1. **Push and prove the tree.** Stop TradeAgent on the box (trap 42), `tools/win-push.sh`, rebuild the app in Release
   there, and prove the pushed tree by hash (a hash of the same file set here and there) BEFORE any figure counts.
   Start the UI agent (`tools/win-agent.sh`); it is NOT RUNNING at dispatch.
2. **Walk the protocol-3 refusal and the repair.** Run the new build against the box's OLD (v2) bridge: quote the
   refusal sentence the status row shows. Then the repair the way the owner would: `Reinstall the bridge` on Checks,
   two-press, through the UI agent; ATAS closed first only if capture works; restart ATAS; the strategy restores STOPPED
   (trap 24) → activate it (trap 40, watch trap 35). Read `proto=3` from the app; quote the health row before and after.
   Fallback if the button cannot be pressed: deploy the DLL by file and record the button as NOT VERIFIED.
3. **`tools/atas-gate`** on the pushed tree, the README's command: exit code and both directions' lines quoted.
4. **The first review's two box items.** (a) The press id shape `TA-op-close-<nonce>-<i>`: open a small SIM position,
   press Close All (two-press, from the app or the operator console), and read the client order id back off ATAS's own
   order: accepted as-is, truncated, or refused. Leave the book flat. (b) Whether a real bridge spends 30–50 s in send
   gate + frame: measure one ordinary order path's real timings from the bridge's and the app's own log lines and say
   how far from `WorstCaseOrderPath` = 50 s the real numbers sit; do not change the constants.
5. **The backlog's box items** (`docs/hardening/briefs/U6-U9-backlog.md`, "For the v0.1.2 box session"): which teardown
   callback fires (`OnStopping` vs `OnDispose`) when the strategy is stopped and when ATAS closes; a 64-char and a
   65-char client-order-id probe against ATAS; mutant B4 (the no-buffer pipe stall) run once; and U9's five adapter
   compile warnings from the Release build of the bridge, listed with file:line and a proposed disposition each.

NOT the cut: no installer build, no `gh release`, no update of the installed app, no product change. Everything is a
measurement; commit any probe or script per item on your branch, no trailers. Leave the box as you found it, with the
new build running instead of 0.1.1: TradeAgent up, ATAS up with the bridge active at protocol 3, the book flat.

## Report — append here, commit it, ≤20 lines: tree hash proven; each item verified by running (quoted) or NOT VERIFIED
with what you tried; the sentences seen in item 2; the id ATAS showed in 4a; the numbers in 4b; what you did NOT do.
