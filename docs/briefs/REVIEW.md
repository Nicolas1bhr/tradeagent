# REVIEW — the milestone review of the money path on `main` before v0.1.2

Fresh Opus reviewer. Your job is to break it, not to confirm it; default to "fails" when uncertain. Read
`docs/HOW-WE-BUILD.md`, `CLAUDE.md` (the four `IAtasAdapter` rules, two-press, operator authority in-process only, the
inbox is data), `docs/CONTRACTS.md`, and the 2026-09-04/05 sections of `BUILD-STATUS.md` (what each landed unit claims).
`export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. Worktree detached at the sha named in
your dispatch: `git worktree add --detach ~/Projects/ai-trading-software-for-mihael-worktrees/review <sha>`, then
`git checkout -b review-probes` there for anything you write. Codex reads the same sha in its own worktree; you do not
coordinate with it. No box. You fix nothing.

**The surface.** `TradeAgent.Gateway` (`TradingGateway`, `GatewayPipeServer`, `GatewayTypes`, the reconciler and the
emergency press), `TradeAgent.Connectors.Atas` (`AtasConnector`, `BridgeProtocol`, `AtasHealth`), `TradeAgent.AtasBridge`
(`CoidWitness`, `AdapterTeardown`, `AtasStrategyAdapter`), `TradeAgent.Provisioning` (`UpdateService.cs` holds
`UpdateService`, `ChecksumManifest`, `ReleaseFeed`, `UpdateSources`; plus `Downloader.cs`), the kill switch
(`StopAiTrading` / `AiTradingStopped`), `TradingMode` (SIM / LIVE_CONFIRM / LIVE_AUTONOMOUS) and the approvals (`Approve*`).

**Hunt, in this order.** (1) Any path by which an agent-facing request gains operator authority, skips LIVE_CONFIRM,
the kill switch or an approval, or updates the app. (2) Any order that can reach the wire without a write-ahead
record that survives a crash, or any record that can leave the unconfirmed set on evidence that is not definite,
about its own target, from its own connector. (3) Any emergency press that can send twice, sweep more than it captured,
or read as confirmed when it is not; any replay that repeats an effect. (4) Any way the updater installs something
whose checksum it did not verify, or replaces the program while an order is unconfirmed. (5) Any input the runtime
reads (settings, manifest, sidecar, pipe frame) whose unreadable or malformed form fails OPEN. (6) Both directions on
every guard you touch: the attack is refused AND the legitimate path still works. (7) Class over instances: two findings
with one root cause are one finding naming the class.

**Method.** Every finding is an executed refutation, not a described one: a test or probe you RAN, quoted, on
`review-probes`. Reading alone yields UNVERIFIED, which you may list separately, ranked. Where the claim in BUILD-STATUS
is a number that depends on a constant (deadlines, drain, chunk sizes), re-derive it from the code once.

**Output.** `docs/REVIEW-<date>.md` in the MAIN worktree (`~/Projects/ai-trading-software-for-mihael`, no git there —
the manager commits it): one table, one line per finding — severity (HIGH = money or authority can be wrong; MED =
fail-open or a guard with no test; LOW = the rest), `file:line`, the check that settles it (the probe's name), and a
one-line "what would fix it". Then a ranked UNVERIFIED list, and "What I did NOT do". Budget: stop after 6 hours of
work or 25 findings, whichever first; the next milestone's review catches what you missed.

## Report — ≤20 lines here in the brief: sha reviewed; counts HIGH/MED/LOW/UNVERIFIED; the three claims you consider
least proven; probes branch tip; what you did NOT do. Verified by running, or NOT VERIFIED.
