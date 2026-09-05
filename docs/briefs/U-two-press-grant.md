# U-two-press-grant — every control that grants authority is two-press; every control that removes it stays one

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md` ("Anything that moves money or removes permission is
two-press"; code-built UI, every value from `Theme.cs`), finding 3 of `docs/REVIEW-2026-09-05b.md` with P1 and P2
quoted, `docs/RESUME-HERE.md`'s Mac UI loop traps (display awake, `caffeinate`, `tools/mac-run.sh`, `tools/mac-shot.sh`),
and the `U-bridge-reinstall` section of `BUILD-STATUS.md` (how a two-press control is built and judged).
`export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. Worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/two-press-grant`, branch `u-two-press-grant` from `main`. Other
builders work `TradingGateway.cs`; you do not touch it — the gateway half is proven by P1/P2 and needs no change.

**The finding.** The Safety page's mode row is `Ui.Secondary`, one press (`DashboardView.cs:669`): one press moves a
live-activated installation from LIVE_CONFIRM to LIVE_AUTONOMOUS, and P1 shows the AI's next order FILLED on a
non-simulated account with the approval row still waiting. The live switch beside it (`:672`) and the real-money
account picker (`SettingsView.cs:406`) are `Ui.Confirm`. Same class: the STOP button is a one-press TOGGLE (`Ui.Big`,
`DashboardView.cs:685`, `MainWindow.cs:659`), so RESUME AI TRADING is one press too, and `docs/USER-GUIDE.md:328` says
"a mis-press costs nothing" — true of STOP, false of RESUME in LIVE_AUTONOMOUS with real money on. P2 bounds it: leaving
a live mode re-arms the live switch, so the one press bites only between the two LIVE modes.

1. **The two live modes on the mode row are `Ui.Confirm`**, the armed sentence in the owner's words ("Confirm: let the
   AI place real orders without asking" for LIVE_AUTONOMOUS; the LIVE_CONFIRM one likewise). OBSERVE and PAPER stay one
   press: they only reduce. The same wherever else the row is built (Settings, onboarding).
2. **The emergency toggle arms only in the RESUME direction:** STOP stays one press; RESUME AI TRADING is two, in every
   place the toggle is built (`DashboardView.cs:685`, `MainWindow.cs:659`).
3. **A risk cap raised is a grant too** (Codex F12): widening any cap on Settings is two-press; lowering stays one.
4. **The sentences:** `docs/USER-GUIDE.md:328` and every guide sentence saying the mode row or resume is one press.
5. **Proof.** A test per control that it is built two-press, the way `BridgeReinstallTests` judges the reinstall button:
   RED first against `main`'s widgets → GREEN; mutant (`Ui.Confirm` → `Ui.Secondary` on the autonomous row) → RED. Then
   the running app on the Mac loop: read the armed label for LIVE_AUTONOMOUS and for RESUME off the screen and quote
   it; if the display cannot be woken or Accessibility refuses the press, NOT VERIFIED and what you tried.

Yours: `DashboardView.cs`, `MainWindow.cs`, `SettingsView.cs` and onboarding only where the same controls are built,
`Ui.cs` only if a variant is missing, `docs/USER-GUIDE.md`, tests. No gateway change. Commit per item, no trailers.
Gate: Release `--no-incremental` → 0 warnings; full suite once to a file (one suite at a time on this Mac; a `Timing`
test that fails while another suite runs → that class alone 3×); names vs `main` 0 removed.

## Report — append here, commit it, ≤20 lines: tip sha; RED → GREEN → mutant; the labels read on screen or NOT
VERIFIED; gate counts; what you did NOT do.

Code tip `8d34072`, this report on top; rebased onto `main` (17 commits, no conflict). **Kept all four earlier commits unchanged** — the factories,
`Ui.ConfirmIf`/`KillSwitch`/`ModeButton`, `RiskPolicy.Widenings`, the guides and the 15 tests are right; I added one commit.
**1–4, re-verified.** RED against `main`'s widgets (the three factories rebuilt with `Ui.Secondary`/`Primary`/`Danger`, nothing
else touched): `Failed: 9, Passed: 6, Total: 15` — `A_real_money_mode…(LIVE_AUTONOMOUS)` → `Expected: null / Actual:
LIVE_AUTONOMOUS`. GREEN at the tip `Failed: 0, Passed: 17`. MUTANT `Ui.Confirm`→`Ui.Secondary`, autonomous row only:
`Failed: 2, Passed: 15` — that same line, plus `The_five_second_refresh…` `Expected: True / Actual: False`.

**5 — running it found two defects, both fixed in `8d34072`.** (a) The armed sentence did not FIT: in the horizontal
`StackPanel` the row ran past its card and the button read `Confirm: let the AI place real or`, cut mid-word. (b) The Safety
page's armed kill switch was a blank red block — arming swaps the control to class `danger`, red text, over its own red fill.
Fixed by a `WrapPanel` mode row and a repaint that sets `Theme.TextOnEmergency` as well as the fill; RED first for both
(`Expected typeof(WrapPanel) / Actual typeof(StackPanel)`; `Expected: White / Actual: Black`) → 17/17, 3×. **Read off the
screen:** chrome *"Confirm: let the AI trade again"*; mode row *"Confirm: let the AI place real orders without asking"*;
Safety switch the same sentence, white on red; the resting row still fits one line. **The presses were NOT a mouse:**
`CGPreflightPostEventAccess()` is False and the Accessibility grant was refused, so a temporary uncommitted in-app probe
raised the same `Click` routed event on the same controls and was then deleted — `git status` clean, nothing carries it.

Gate: Release `--no-incremental` **0 warnings, 0 errors**; the class 3× `Failed: 0, Passed: 17`; full suite in Release once,
alone (`pgrep testhost` 0 first) — **Unit 236 + Fault 250 + Integration 584 = 1070, 0 failed**. Names vs `main`: **0 removed,
15 added**. Secret scan clean. NOT done: no mouse press, no box, no Windows, no gateway change, no push, no merge.
