# U-holdout-later — a held window never shrinks while a campaign judges on it: moving a cutoff LATER is refused in the owner's words, and the words that called it harmless are corrected
**Arrow closed:** the evidence protection — `docs/COUNCIL.md`:150 "holdout data the research process cannot reach", :231 "a leaked holdout cannot become unseen". Found MEASURED by seat A's
U-holdout-campaign survey (2026-10-08, probe RED at `ea88e72b`); ruled urgent by the orchestrator. **Depends on:** `U-bar-holdout` (LANDED `72bcd8dd`). **Then:** `U-holdout-campaign`,
`U-holdout-served`. **Protects:** the holdout, and the verdict's record of what was private (`strategy_verdict.holdout_from`). **No schema change, no rung.**
**Today** (main `c05b5d14`): the owner's press may move a cutoff LATER — `DatasetStore.SetHoldout` refuses only an EARLIER one (`DatasetStore.cs:597-606`) — and `TradingGateway.SetHoldout`
(`TradingGateway.cs:1636-1660`) leaves the open campaign as it was (`:1649-1650`). The bars' and the tape's rules then serve `[old, new)` to research (they read `dataset.holdout_from`, `TapeHoldout.cs`),
while the referee still judges from `campaign.HoldoutFrom`, the cutoff at opening (`Referee.cs:348-352`), and a renewal keeps it for the whole lineage (`CampaignStore.cs:408-425`, "not a different
holdout"; `Close` has no other caller). So every judgement after the move reads minutes research may have read, and its verdict row records the old cutoff as what was private. The words read
backwards: `DatasetStore.cs:576-578` ("un-holds bars … which leaks nothing"), its owner-facing refusal `:604-606` and `CONTRACTS.md:3318` ("withholds bars nothing has read").
**The probe** (`~/Projects/ai-trading-software-for-mihael-worktrees/fleet/tmp/s-holdout-campaign-survey/SHoldoutCampaignProbe.cs`, `Probe_a_cutoff_moved_later_releases_minutes_the_referee_still_judges_as_held`;
log `probe-run-3.log`): `L.1 the owner moves A's cutoff from 01:00:00Z to 01:30:00Z: Ok=True … campaign.HoldoutFrom=01:00:00Z dataset.holdout_from=01:30:00Z` · `L.2 data-bars BTCUSDT
[01:00, 01:29] as the Research Director after the move: SERVED 30 bars` · `L.4 verdict P1 --dataset A: … verdict=promoted` · `L.5 the referee's run over A: window_from=01:00:00Z bars=60`.
**The rule (seat A's decision, on the orchestrator's view):** a cutoff never moves while a campaign judges from it — EARLIER stays refused (its words unchanged in substance), LATER is now refused
too, while the dataset has an open campaign (in this build, from the first press on: a lineage is renewed, never closed). The refusal writes NOTHING (no cutoff, no class, no campaign) and tells the
owner, in his words, that campaign N still judges strategies on every bar from the first cutoff, that a later cutoff would show the AI bars those judgements use, and that holding back a different
period means downloading a fresh copy of the history and holding months back on it. The same instant again stays a no-op answering Ok. It lives where the cutoff is WRITTEN, so no caller can
pass it (the builder's call between `DatasetStore` reading the campaign ledger and a required argument; stated in the report). The referee keeps judging from `campaign.HoldoutFrom`, unchanged.
**Observable result:** with campaign 1 open on A, the owner's later press answers Ok=False in those words and changes nothing: A's cutoff, the bars' and the tape's windows, the campaign and the
Settings line stay as they were; `data-bars` over `[old, new)` stays `HOLDOUT_WITHHELD` for every role.
Read first: `CLAUDE.md`; `DatasetStore.cs:540-615`; `TradingGateway.cs:1610-1662`; `CampaignStore.cs:315-460`; `Referee.cs:300-360`; `SettingsView.cs:790-905`; the probe and `NOTES.md` beside it.
Items, one commit each:
1. The refusal (the rule above), with `DatasetStore.SetHoldout`'s comment (`:566-584`) saying why both directions are refused, and its earlier-refusal words (`:604-606`) losing "Moving it later is allowed".
2. The words, the right way round: `CONTRACTS.md:3310-3325` (the holdout's own section); `docs/USER-GUIDE.md:424-437` "Holding months back" (a cutoff never moves once pressed; it holds every pair's bars
   and the tape over its window since U-bar-holdout; a different period = a fresh download); and U-bar-holdout's leftover — the `data-list` reply's note (`GatewayPipeServer.cs:2689`, `:2696-2704`:
   "No holdout applies to them either … every forward bar post-dates every freeze", and "refuse any window that reaches it" naming only the dataset's own cutoff), the same premise in
   `ForwardBars.cs:20-24` and `GatewayPipeServer.cs:2855`'s comment — all stating the every-dataset rule U-bar-holdout landed.
Tests: (a) `A_later_cutoff_is_refused_while_a_campaign_judges_from_the_first` (the probe's L legs as a test: Ok=False in the owner's words, the cutoff, class and campaign unchanged, `data-bars` over
`[old, new)` refused for the Research Director and a caller with none; RED on the base, quoted); (b) `The_same_cutoff_again_is_still_a_no_op` and the earlier press refused as today; (c) the refusal
names the campaign, its first cutoff and the fresh-download path; (d) `data-list`'s reply no longer says forward bars are exempt (its note names the every-dataset rule). Rewritten under this
protection, named in the report: `CampaignLedgerTests`' second press (`:225-230`, "moves the cutoff later and does NOT open a second campaign") and
`HoldoutLedgerTests.A_cutoff_may_be_moved_later_because_that_withholds_bars_nothing_has_read` (`:113`; its name states the false premise — a disclosed rename is accepted).
**Mutant**, quoted red: the later-move refusal removed ⇒ (a). **Answer first:** does any caller other than the owner's press (`SettingsView.ApplyHoldout`, `:858-876`) write a cutoff in `src/`?
Gate and report per `docs/HOW-WE-BUILD.md` pass 1 and `docs/FLEET.md` "The builder pass": rebase on `main` first; Release `--no-incremental` 0 warnings; Unit, Fault, Integration 0 failed, touched
classes 3×; CI via `fleet/bin/ci-dispatch.sh` (run id, each job); names 0 removed (both sizes) or the disclosed rename; the tests box run or its NOT RUN line; `## Report` ≤ 20 lines. No push to `main`.
