# REVIEW — the second milestone review of the money path on `main` at `d92a61b`, before v0.1.2 is cut

Fresh Opus reviewer. Your job is to break it, not to confirm it; default to "fails" when uncertain. Read
`docs/HOW-WE-BUILD.md`, `CLAUDE.md` (the four `IAtasAdapter` rules, two-press, operator authority in-process only, the
inbox is data), `docs/CONTRACTS.md`, `docs/REVIEW-2026-09-05.md` (the first review: its findings are FIXED on this sha by
the units named in the 2026-09-05 sections of `BUILD-STATUS.md`; you verify the fixes hold, you do not re-report them)
and those sections (what each landed unit claims). `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no
`timeout`. Worktree detached at `d92a61b`: `git worktree add --detach ~/Projects/ai-trading-software-for-mihael-worktrees/review d92a61b`,
then `git checkout -b review-probes` there for anything you write. Codex reads the same sha in its own worktree; you do
not coordinate with it. No box. You fix nothing. One full suite at a time on this Mac; another leg may be running one.

**The surface, in the order the first review left it untouched.** (1) The approval chain (`Approve*`;
`ApprovalReauthorizationTests` covers it — find the gap that reading did not). (2) The material ledger: `material` is
written only by the scanner, `material_note` by the agent; any path by which a note touches a material row, or inbox
text becomes permission. (3) `ForceResolve`, `Decline` and the reconciliation override: a flagged terminal record, the
override's two presses, and what resumes trading. (4) `BridgePipeAuth`, the protocol-3 handshake, `AtasConnector` /
`BridgeProtocol` / `AtasHealth`: a wrong peer trusted, a refusal that parks either side, a status row that outlives the
truth. (5) `CoidWitness` / `AdapterTeardown` / `AtasStrategyAdapter` (the ATAS hunks compile only on the box: read
them, probe the extracted seams): an order reaching the wire without a committed witness record; `SupportsOrderHistory`
without a coverage watermark; the obsolete synchronous calls wedging the frame loop. (6) The App outside `DashboardView`:
a one-press route to money or permission, or anything that opens a console. (7) Every fix unit's guard, both directions:
the attack refused AND the legitimate path still works; a bypass of a guard is a finding on its class.

**Hunt** the same five things as the first review: agent authority gained; an order on the wire without a crash-surviving
record, or a record leaving the unconfirmed set on evidence that is not definite, about its own target, from its own
connector; a press that sends twice or reads confirmed when it is not, a replay that repeats an effect; an install over
unconfirmed work or without a verified checksum; any input whose malformed form fails OPEN. Class over instances.

**Method.** Every finding is an executed refutation: a test or probe you RAN, quoted, on `review-probes`. Reading alone
yields UNVERIFIED, listed separately, ranked. Re-derive once any number in BUILD-STATUS that depends on a constant.

**Output.** `docs/REVIEW-2026-09-05b.md` in the MAIN worktree (`~/Projects/ai-trading-software-for-mihael`, no git
there — the manager commits it): one table, one line per finding — severity (HIGH = money or authority can be wrong;
MED = fail-open or a guard with no test; LOW = the rest), `file:line`, the probe that settles it, one line "what would
fix it"; the probe output quoted below the table; then a ranked UNVERIFIED list and "What I did NOT do". Budget: stop
after 6 hours or 25 findings, whichever first; the next review catches what you missed.

## Report — ≤20 lines here in the brief: sha reviewed; counts HIGH/MED/LOW/UNVERIFIED; the three claims you consider
least proven; probes branch tip; what you did NOT do. Verified by running, or NOT VERIFIED.

## Report — reviewer (fresh Opus), the second milestone review

- **Sha** `d92a61b534408d623129edcd95d39c0364abbd0d`, clean. **HIGH 3 · MED 2 · LOW 1 · UNVERIFIED 7.** Table, eleven quoted probe outputs, the guards that held and the ranked UNVERIFIED list are in `docs/REVIEW-2026-09-05b.md`; its `## Codex` heading is left empty for you.
- **HIGH 1** `TradingGateway.cs:3052` — the Dashboard override has no dispatch lease: "No order exists" writes a terminal state onto a live DISPATCHING row, trading resumes, and the broker's FILLED is discarded (`LateDefiniteSettle` rescues only UNKNOWN/RECONCILING). **Verified by running** `P3…`: broker holds ES 1, record CANCELLED unflagged, `already_settled`. Review-1 finding 1 through the button instead of the reconciler.
- **HIGH 2** `TradingGateway.cs:2497` — the press's drift re-read sees a landed fill, not a closing order in flight: an agent `close` inside the connector call plus the owner's Close All. **Verified by running** `P6…` → 3 orders, `position at the end: ES -2`; bounded by `P6b…`.
- **HIGH 3** `DashboardView.cs:669`, `:685` — the mode row and the STOP/RESUME toggle are one-press controls that GRANT authority (LIVE_CONFIRM → LIVE_AUTONOMOUS beside a two-press live switch). **Verified by running** `P1…` + `P2…` for the gateway half; the widget is READ, **NOT VERIFIED** on screen.
- **MED 4** `Downloader.cs:62/70/105` — a stale `.part` is concatenated into the finished file and, with no checksum, run elevated (`Prerequisites.cs:118`, `:136`). **Verified by running** `P4c…` against a real socket.
- **MED 5** `MaterialScanner.cs:67` — the agent's own working directory contains `inbox/`, so it can author a `material` row saying the owner handed it the file. **Verified by running** `P5a…`. **LOW 6** `MaterialStore.cs:45` — a removed row returns carrying the old sha. **Verified by running** `P5b…`.
- **Three least proven:** (a) that a real ATAS dispatch outlives the 70 s bound — **NOT VERIFIED**; (b) `SupportsOrderHistory` with the watermark skipped when `ClearCachePeriod <= 0` (`AtasStrategyAdapter.cs:664`, `:936`) — read only, **NOT VERIFIED**, box; (c) finding 3 as a mis-press on a real screen — **NOT VERIFIED**.
- **Guards checked and holding, verified by running:** U-stranded's lease `P3`, U-gates' re-check `P6b`, the live re-arm `P2`, zip/tar escape refused by the framework `P4a/P4b`, every BUILD-STATUS constant re-derived `P7`.
- **Probes tip `review-probes-b` @ `80f19f0`** — 6 commits, 3 test files, no product file. `review-probes` was already the FIRST review's branch at `b952851`, unmerged, and was NOT moved. Gate at `0768ec2`, Release: build 0 warnings/0 errors; **224 + 244 + 582 = 1050, 0 failed** (1040 on `main` + 10 probes); P6b's class 2/2 after that run.
- **Did NOT do:** no box, no real ATAS, no money, no UI run, no mutation testing, no push, no merge; no probe of `BridgePipeAuth`, `AtasHealth`, `CoidWitness`, `AdapterTeardown`, `ChatView`, `InboxView`, `OnboardingView`, `AgentSession`; entered no other worktree; in the main worktree wrote only `docs/REVIEW-2026-09-05b.md` and this block, with no git commands there.
