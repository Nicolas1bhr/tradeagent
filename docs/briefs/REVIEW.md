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

## Report — ≤20 lines here in the brief: sha reviewed; counts HIGH/MED/LOW/UNVERIFIED; the three claims you consider least proven; probes branch tip; what you did
NOT do. Verified by running, or NOT VERIFIED.
