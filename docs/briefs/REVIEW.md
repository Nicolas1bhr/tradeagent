# REVIEW — the third milestone review of the money path on `main` at `c441120` (2026-09-16), before any release is cut

Fresh Opus reviewer. Your job is to break it, not to confirm it; default to "fails" when uncertain. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md` (the four
`IAtasAdapter` rules, two-press, operator authority in-process only, the inbox is data), `docs/CONTRACTS.md` (every unit's choices, binding), `docs/COUNCIL.md:14-15,
32-33,55-63`, `docs/REVIEW-2026-09-05b.md` (the second review: its findings are FIXED on this sha; you verify the fixes hold, you do not re-report them) and the
`BUILD-STATUS.md` sections from `## 2026-09-06 — U-life landed` to the end (what each landed unit claims — the file is 6,200 lines; read only those sections).
`export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. Worktree detached at `c441120`: `git worktree add --detach
~/Projects/ai-trading-software-for-mihael-worktrees/review c441120`, then `git checkout -b review-probes-c` there for anything you write (`review-probes` and
`review-probes-b` exist and are not yours). Codex reads the same sha in its own worktree; you do not coordinate with it. No box. You fix nothing. One full suite at a
time on this Mac; another leg may be running one.

**The surface, in the order it landed since the second review.** (1) The dispatch gate on ONE position reading (`PlaceAsync` → `_dispatchGate` → cap →
unresolved reducer → `LossBudgetOrThrow` → the allocation ceiling → `TryCreate`) and the approval path that re-runs it: a race, a reading reused, an order admitted
between a breach write and its refusal. (2) The loss-budget line end to end — `LossWatchAsync` (two DISTINCT pulls, epoch, executable side), the breach record (write-once
in code, `kv` an UPSERT), the app-initiated flatten (`op-budget-cancel-` settled before `op-budget-close-`, `ReductionOnlyOrThrow`, resolution only behind a fresh flat
read-back, crash recovery keyed on the absent outcome record), the reopen (`OpenClosures`, `EligibleAt` from the record's snapshot, the receipt only the tick writes
through `AddKvOnce`, the clock high-water mark, `APPROVAL_PREDATES_LOSS_BREACH`), strikes and the two-press release with a note, the settings snapshot: any path by
which a scope trades while closed, reopens without earning it, a release that reaches the wire, a widened or zeroed budget that reopens, a clock that reopens, a
PAPER record answering for LIVE, a strike that never counts. (3) The allocator: `strategy_allocation` immutable, written only for a `promoted` Standing through a
two-press card; `PlaceIntent.StrategyVersionId` and the two request columns; the ceiling refusing past `max_quantity`/`max_notional`: an order attributed to a version
that did not cause it, a ceiling passed by two callers, a withdrawn promotion still allocating. (4) Freshness and promotion bounds: the referee refusing a bound-less
version at the verdict; the dispatch gate refusing a stale decision or an unclosed bar; a program that declares bounds it does not honour. (5) Containment and the
harness: the agent process in its job with a clean environment, known by launch on the pipe, refused in the armed live configuration; the app-owned worker with the
key in memory only: any route from the agent-facing pipe to mode, kill switch, live activation, approvals, allocation, release, or the app's own update. (6) The
council boundary and the relay: a disposition that reaches the order path, a task doubled or lost, a director's text that becomes permission. (7) The venue catalog
and the second candle source: an instrument fact an agent typed, a bar that lies about what it is, a wake on data that was never validated. (8) Every fix unit's guard
since 2026-09-05, both directions: the attack refused AND the legitimate path still works; a bypass of a guard is a finding on its class.

**Hunt** agent authority gained; an order on the wire without a crash-surviving record, or a record leaving the unconfirmed set on evidence that is not definite,
about its own target, from its own connector; a press or an app leg that sends twice, reverses, or reads confirmed when it is not; a closed scope that trades; a
replay that repeats an effect; an install over unconfirmed work or without a verified checksum; any input whose malformed form fails OPEN. Class over instances.

**Method.** Every finding is an executed refutation: a test or probe you RAN, quoted, on `review-probes-c`. Reading alone yields UNVERIFIED, listed separately,
ranked. Re-derive once any number in `BUILD-STATUS.md` that depends on a constant (15 s, 60 s, 24 h, 7 days, `PressBudgetFor`). **Output.** `docs/REVIEW-2026-09-16.md`
in the MAIN worktree (`~/Projects/ai-trading-software-for-mihael`, no git there — the manager commits it): one table, one line per finding — severity (HIGH = money
or authority can be wrong; MED = fail-open or a guard with no test; LOW = the rest), `file:line`, the probe that settles it, one line "what would fix it"; the probe
output quoted below the table; then a ranked UNVERIFIED list and "What I did NOT do". Budget: stop after 6 hours or 25 findings, whichever first.

## Report

Sha `c441120`. **HIGH 5 · MED 3 · LOW 0 · UNVERIFIED 7**, every finding executed; table, probe output, ranked UNVERIFIED and "What I did NOT do" in `docs/REVIEW-2026-09-16.md`.
HIGH: (1) a loss-budget closure on a venue-qualified symbol is invisible to every reader and the closed scope trades — `LossBreach.ScopeOf` splits the key on `:`;
(2) the fill ledger is scoped to no account and no platform, so one account's profit keeps another trading past its budget and one account's loss closes an account that never traded;
(3) a PAPER breach flattens the LIVE book after a platform switch — real cancels and closes sent on an installation with no budget set;
(4) `ApproveAsync` drops the capital gate and the unresolved-reducer refusal, so a parked order goes out over a withdrawn ceiling and a parked reduce doubles a close;
(5) an expired, revoked or turn-ended launch grant still places orders on the connection it said hello on.
MED: (6) one forward clock step ends a closure on the next tick, against what `CONTRACTS.md` and `LossReopen` both claim; (7) the per-order limits are decided above the
dispatch gate and never re-asked at the wire; (8) the two agreeing pulls are keyed by the UTC day, so a pair straddling midnight writes no breach at all.
The manager's addendum, Codex's four unexecuted hypotheses: **all four VERIFIED, none REFUTED** — C1 = finding 5, C2 = finding 3, C3 = finding 4 (both arms),
C4 = finding 6, rated MED because nothing an agent can reach moves the clock, and recorded as contradicting the contract rather than as a documented limit.
Least proven, in order: (a) whether any platform TradeAgent will attach names an instrument or an account with a colon in it — finding 1's defect is executed, its
reachability on ATAS is not; (b) that an exit throwing between its close and its record gets a SECOND close (UNVERIFIED 1, read only); (c) everything the bridge half
does — `AtasStrategyAdapter`, `CoidWitness` and `AdapterTeardown` were not opened at all.
Probes branch `review-probes-c` @ `58aa4fa`, two test files, no product change. Gate on that tip, Release: build `--no-incremental` → **0 warnings, 0 errors**;
full suite → **Unit 1132 + Fault 362 + Integration 669 = 2163 passed, 0 failed, 1 skipped** (2150 on `main` plus 13 probes); the probe classes alone 12/12 and 1/1.
NOT done: no box, no ATAS, no real money, no screen, no mutation testing, no bridge code read; did not probe the updater, the Doctor, the relay's task delivery, the
venue override loader, the second candle source, the backtest or the referee; fixed nothing, pushed nothing, entered no other worktree.
