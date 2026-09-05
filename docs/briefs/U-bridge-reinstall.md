# U-bridge-reinstall — the repair the protocol-3 refusal names, built into the app

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md` (the user never sees a terminal; every colour, size
and gap comes from `Theme.cs`; the dashboard tree is built once and updated in place), then the U8 section of
`BUILD-STATUS.md` (the finding), `src/TradeAgent.Connectors.Atas/AtasInstallation.cs` (`InstallBridge`, ~:191),
`src/TradeAgent.App/DashboardView.cs` (~:910-919, the Checks repair text with no buttons), `OnboardingView.cs` (the
setup step that installs the bridge today), and `Doctor.cs` ("Press Install bridge."). `export PATH="$HOME/.dotnet:$PATH"
DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`; full suite 8–12 min in Release. Fresh worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/u-bridge-reinstall`, new branch `u-bridge-reinstall` from `main`.

**The defect.** When the app refuses an old bridge (protocol < 3) it tells the owner to "reinstall the add-on from
TradeAgent", and `Doctor.cs` says "Press Install bridge." — but once setup has completed there is no such control:
setup renders only while onboarding is incomplete (`MainWindow.cs:183`), `Onboarding.Clear`'s only caller is the
wizard's Back (`OnboardingView.cs:349`), and Checks prints text with no buttons. The v0.1.2 update will refuse every
installed bridge until it is redeployed, and the owner cannot do it.

1. **A "Reinstall the bridge" button on the Checks page**, shown whenever the bridge row is refused, missing, or the
   wrong protocol, and always available from Settings. One press, then a second press to confirm (it stops trading
   through the bridge while it runs — say so in the button's confirmation text); it runs the same `InstallBridge` the
   setup step runs, reports success or the reason in the app's own words, and the bridge row re-derives its status.
   ATAS may hold the DLL open: if the copy is refused, the sentence says to close ATAS and press again — no other
   instruction. Nothing here opens a terminal or shows a path.
2. **The sentences agree.** The protocol refusal sentence, the Checks repair text and `Doctor.cs` all name the control
   that now exists, with its on-screen label; the two places where the app still says "add-on" (`OnboardingView.cs:941`,
   `BridgeProtocol.cs:167`) say "bridge".
3. **`docs/USER-GUIDE.md`** gains the paragraph (owner's words, no paths) and `docs/DEPLOYMENT.md`'s "not yet walked"
   note for the bridge redeploy points at the button.

Yours: `DashboardView.cs`, `SettingsView.cs`, `Theme.cs` if a token is missing, `AtasInstallation.cs`, `Doctor.cs`,
`BridgeProtocol.cs` (the sentence only), the two docs, tests. Not yours: the gateway, the connectors' trading paths,
`CoidWitness*`. Red-first where a test can reach it (the install call, the status re-derivation, the refusal sentence);
the button itself is seen on the Mac loop (`tools/mac-run.sh`, `tools/mac-shot.sh`) — attach the screenshot path in
the report. Commit per item, no trailers, no push, no other worktree. Gate: Release `--no-incremental` → 0 warnings;
full suite in Release → 0 failed. No box: the real ATAS copy-while-open case stays NOT VERIFIED and says so.

## Report — append as you go, commit with each item, ≤20 lines: tip sha; per item RED → GREEN → mutant or the
screenshot; final counts; what you did NOT do. Verified or NOT VERIFIED.

Branch `u-bridge-reinstall` off `main` `421b5d8`. No push, no other worktree, no Windows box.

1. **The button — DONE.** RED: `tests/.../BridgeReinstallTests.cs` would not compile — `ErrorCode.ATAS_BRIDGE_IN_USE`,
   `Labels.ReinstallBridge`, `AtasHealthReporter.Forget`, `AtasHealth.RepairOffered` did not exist (13 CS errors).
   GREEN: 6/6. Mutant `Forget() { }` → RED `Assert.DoesNotContain() Failure: Sub-string found`. Seen on the Mac loop:
   `.../scratchpad/ui-checks.png` — the card and `Reinstall the bridge` on Checks, shown because the bridge row is
   FAILED and not installed. Settings shot: `.../scratchpad/ui-settings.png`.
2. **The sentences agree — DONE.** RED: 2 fails — `Not found: "Reinstall the bridge"` (the refusal) and
   `Found: "press Retry"` (a button no screen has ever had). GREEN: 218 unit. Mutant: old refusal text back →
   RED `Not found: "Reinstall the bridge"`. Beyond the brief's two "add-on" spots I found three more live
   sentences (`AtasConnector.cs` `PendingHello`, `Silent`, `PresentedNoProof`) and fixed the text only; and
   two catalogue repairs naming a phantom Retry (`ATAS_NOT_FOUND`, `AI_INSTALL_FAILED`).
