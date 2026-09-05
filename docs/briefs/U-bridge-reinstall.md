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

Gate run at `e658947` (4 commits, rebased onto `main` `6bd009e`); this report sits on top. No push, no box.
1. **The button — DONE.** RED: `BridgeReinstallTests.cs` would not compile, 13 CS errors — `ATAS_BRIDGE_IN_USE`,
   `Labels.ReinstallBridge`, `AtasHealthReporter.Forget`, `AtasHealth.RepairOffered` did not exist. GREEN 6/6.
   Mutant `Forget() { }` → RED `Assert.DoesNotContain() Failure: Sub-string found`. Its unexpected-failure
   sentence is `UNKNOWN_ERROR`, whose repair no longer names a "Diagnostics screen" that does not exist. Seen:
   `/private/tmp/claude-501/-Users-nicolasbeeckman-Projects-ai-trading-software-for-mihael/7fe21c6c-d09b-48d0-a0fe-e2b7be859c31/scratchpad/ui-checks.png` (the card and `Reinstall the bridge` on Checks), `/private/tmp/claude-501/-Users-nicolasbeeckman-Projects-ai-trading-software-for-mihael/7fe21c6c-d09b-48d0-a0fe-e2b7be859c31/scratchpad/ui-settings.png`.
2. **The sentences agree — DONE.** RED 2: `Not found: "Reinstall the bridge"`; `Found: "press Retry"`. GREEN 218
   unit. Mutant: old refusal text back → RED `Not found: "Reinstall the bridge"`. Past the brief's two "add-on"
   spots I fixed, text only, three more live sentences (`AtasConnector.cs` `PendingHello`, `Silent`,
   `PresentedNoProof`) and two catalogue repairs naming a Retry button no screen ever had (`ATAS_NOT_FOUND`;
   `AI_INSTALL_FAILED` → `Try again`, `OnboardingView.cs:597`).
3. **The docs — DONE.** USER-GUIDE: the "no button for that repair yet" paragraph and the "Still not finished"
   bullet become how to press it — owner's words, no paths. DEPLOYMENT: the redeploy note and §5 point at the
   button; its "not yet walked" bullet names what is NOT verified. MONITORING-PHASE too — its row-4 grep string
   was a sentence I had just deleted.
**Gate at the tip, Release:** build `--no-incremental` → 0 warnings, 0 errors; suite 218+207+569=994, 0 failed; test names vs `main` 7 added, 0 removed; scan clean.
**NOT VERIFIED:** the real ATAS-holds-the-DLL refusal — no box; the test stands a destination the copy cannot
overwrite in its place. Armed label and result sentence never photographed — no Accessibility permission in this
shell, so both shots are resting state, taken with a capture-only edit, reverted and in no commit. **NOT done:**
three "press Retry" sentences in `Prerequisites.cs`, two `Versioning.cs` comments quoting the retired refusal.
