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
