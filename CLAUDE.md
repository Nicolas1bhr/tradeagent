# Working on TradeAgent

**Audience: whoever picks this up next — a human, or a Claude Code session.**

## Start here

**`docs/RESUME-HERE.md` is the resume point.** It says what to do next, what is still open, and which
traps have already been paid for. Read it before planning anything.

`BUILD-STATUS.md` is the honest record: every claim in it is either verified with the output quoted,
or explicitly marked not verified. **Keep it that way.** If you cannot quote the run, write
"NOT VERIFIED" and say what you tried. There is no third category, and the words *should work*,
*looks correct*, *probably* and *I believe* do not belong in it.

## The product rule that overrides convenience

**The user never sees a terminal. Not once.** That is what is being sold — the underlying capability
is already available to anyone willing to use a shell. It forbids the obvious implementation of
several features:

- The agent CLI is never launched with a visible console. `CreateNoWindow = true`, always. The
  conversation is hosted *inside the app* over the CLI's non-interactive mode.
- Sign-in runs headless: capture the login command's output, pull the URL out, open the browser from
  the app. Where a runtime has no headless sign-in at all, the app takes a pasted key in its own
  window rather than printing an instruction that means "open a terminal".
- Dependencies install themselves. A "here is the download page" fallback whose instructions are
  `npm install -g ...` breaks the promise exactly as badly as a console window does.
- The only burden the user may bear is clicking Yes on a Windows permission prompt — so prefer
  per-user installs into `%LOCALAPPDATA%\TradeAgent\tools` that need no elevation at all.

## The safety rules that outrank making it compile

This software places real orders with real money. Four rules, stated on `IAtasAdapter` and meant
literally:

1. Carry `ClientOrderId` onto the broker order and read it back. If a backend cannot round-trip a
   client identifier, report `SupportsClientOrderId = false` and accept that the gateway refuses
   fully automatic live trading. **Do not fake it.**
2. Order history must really reach back to the timestamp asked for. If it cannot, report
   `SupportsOrderHistory = false`. A partial history is worse than none: it makes "this order does
   not exist" look provable when it is not.
3. `AtasRejectedException` is for a definite broker refusal and nothing else. Timeouts, disconnects
   and anything ambiguous must propagate so the gateway records UNKNOWN and reconciles.
4. Never place orders by driving a user interface. Programmatic API only.

Operator authority — mode, kill switch, live activation, approvals — is in-process only and is not
reachable from the agent-facing pipe. An agent that wants more permission has nowhere to ask. Keep
it that way.

## Building and verifying

`dotnet` is not on PATH on the dev Mac: `export PATH="$HOME/.dotnet:$PATH"`.

```bash
dotnet build TradeAgent.sln
dotnet test TradeAgent.sln        # 91 tests; green is a precondition for packaging, not a report
tools/mac-run.sh                  # run the UI locally — seconds per iteration
tools/mac-shot.sh /tmp/ui.png     # capture only the app window
```

Windows is where the claims get proven. `tools/README.md` has the setup; `tools/probe` is the
harness behind the two headline claims and is re-runnable.

## Conventions

- **Code-built UI, no XAML, no MVVM framework.** Every colour, size and gap comes from `Theme.cs`.
  If a value is not in the theme it does not belong on the screen — that is the only thing keeping a
  hand-built UI from drifting into forty slightly different greys.
- **The dashboard tree is built once and updated in place.** Rebuilding it on the five-second refresh
  made diagnostics vanish mid-read, reset scroll position and silently disarmed half-pressed
  confirmations. Rebuilding a tree is not a refresh.
- **Vendor commands are data, not code** (`runtimes.json`, `atas.json`). These vendors change their
  CLIs on their own schedule; a wrong command should be a one-line data fix, not a rebuild.
- **Anything that moves money or removes permission is two-press.**
- Do not commit credentials, host names or the contents of `%LOCALAPPDATA%\TradeAgent`.
