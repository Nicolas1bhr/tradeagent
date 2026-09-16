# U-review-med — the three MED findings of the third review as one batch: a forward clock step, the per-order limits at the wire, the sighting keyed by scope
Read `docs/HOW-WE-BUILD.md` (MED and LOW together become one batch unit), `CLAUDE.md`, `docs/REVIEW-2026-09-16.md` findings 6, 7 and 8 with their probes (`C4`, `P2`,
`P4`), `docs/CONTRACTS.md` (`U-reopen-1`: "a step forward is ordinary and lengthens nothing"; "Every gate is evaluated at the moment of dispatch, after the awaited
reads"), then `src/TradeAgent.Gateway/TradingGateway.cs:2508,2605-2660` (the clock high-water mark refusing only `at < HighWater`), `LossReopen.cs:135-148` (`ClockKey`
and its claim), `:3793` (`RiskCheckOrThrow` above `_dispatchGate.WaitAsync :3815`), `:4514-4518` (`ReauthorizeAtDispatchOrThrow`), `:3301-3316,2368-2378` (`Confirmed`
keyed by `LossBreach.DayKey(account, at)`), the probe tests on branch `review-probes-c` @ `58aa4fa` — your RED tests: bring them over, quote them red on your base,
make them green. Branch `u-review-med`, worktree `~/Projects/ai-trading-software-for-mihael-worktrees/U-review-med`, from `main`, rebased AFTER `U-scope-identity`
lands (item 3 touches the same code). **Money path:** a test RED before every guard and ONE mutant, quoted, over `RecordingConnector`. No schema. No box, no money.

1. **A closure is held against a monotone reading as well as the wall clock.** The clock mark carries a monotone reading (`Environment.TickCount64` or a `Stopwatch` seeded
   at the breach and persisted with the mark); eligibility requires the closure to have RUN on both, and a forward wall-clock step larger than a few watch intervals
   with no matching monotone elapse is suspect exactly as a backward one is (`loss_clock_suspect`, refused, said in `status`); across a restart the monotone term restarts
   conservatively from the restart. `CONTRACTS.md` and `LossReopen.ClockKey` say what the code now keeps. RED: `C4` (closed 12:00:40Z, one `MoveTo(+2 days)`, one tick,
   receipt written, a buy SENT). Mutant (the monotone term dropped): the same.
2. **The per-order limits are asked again at the wire.** The allowlist, `MaxOrderQuantity`, `MaxNotionalPerOrder` and the quote-age rule are re-run inside the gate
   beside the four position gates, on the reference price the record already carries, for placements AND approvals; the contract heading becomes true. RED: `P2` (ES off
   the allowlist and the cap cut to 1 while the order sits in the gate's read; `ES Buy 5` reaches the broker). Mutant (re-run above the gate only): the same.
3. **The sighting is keyed by scope, the day by the confirming pull.** `Confirmed` files the first sighting under `(connector, account, symbol)` — the scope, not the
   day's key — and the record's `Day` is taken from the CONFIRMING pull's instant at `Compose`, so a pair straddling midnight confirms; the day that lost the money is
   the day the record names. RED: `P4` (both pulls `reached=True`, `closed=[]`, no breach row at 00:00:10Z). Mutant (the day taken from the first pull): a breach
   confirmed at 00:00:10Z closes YESTERDAY, which reopens at midnight tonight — assert the day.
Not this unit: a fifth gate; the UNVERIFIED list of the review; anything on the bridge.
Gate and report as `U-flatten-1`; every probe you brought over named with its new name and its red quoted.
