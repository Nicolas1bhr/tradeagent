# Manager log — session resumed 2026-09-03 (evening), top-level manager on Fable, legs on Opus + Codex

Pick-up point was `HANDOFF-2026-09-03.md`. `main` = `origin/main` = `9fd5eb7` (the handoff commit on top of `3f1d8f2`).
No stray worktrees; every unit branch tip matched the handoff table. Tailscale on the Mac reports "stopped"; the box is
unreachable and not needed until the v0.1.2 cut (integration step 5). Toolchain checked: dotnet 10.0.400, codex-cli
0.144.1, gh 2.92.0.

## Structure for this session

- Manager (this session, Fable) directs and integrates; it writes no product code. Every build/verify leg is an Opus
  general-purpose agent; Codex runs as a background `codex exec` (not a Claude leg). Hard cap: **2 heavy Claude legs at
  once** (four rate-limit kills last session).
- One worktree per leg under `~/Projects/ai-trading-software-for-mihael-worktrees/` (durable; never the scratchpad):
  `u14-build` (branch), `u2a-rebase-probe` (probe branch), `u2a-verify-r4` and `u2a-codex-r4` (detached at the sha).
- Briefs are committed to `docs/hardening/briefs/` so a killed leg can be re-briefed from disk; legs write their records
  into `docs/hardening/records/` in the main worktree and the manager commits them.

## Decision: rebase U2a BEFORE verifying round 4 (not after, as the handoff's step 1 had it)

Claims attach to a sha. Verifying `5c716aa` and then rebasing onto a `main` that changed `GatewayTypes.cs` (U2b) would
leave the merged combination unverified. Probe rebase in `u2a-rebase-probe`: **clean, no conflicts**, new tip `e91293e`,
21 commits ahead of `9fd5eb7`. Full suite on `e91293e`: see the next entry. The branch `u2a-pipe-hardening` is moved to
`e91293e` only after that suite is green; the pre-rebase tip `5c716aa` stays reachable by sha.

## Timeline

- 17:40 U14 builder (Opus) dispatched on `u14-build` @ `a8b3fb0` with `briefs/U14-r4-item6-build.md`.
- 17:40 U2a rebased build + full suite running in `u2a-rebase-probe` (background).
- 17:52 Rebased U2a (`e91293e`): `dotnet build` exit 0; full suite **390 green / 1 red** (FaultTests 75, UnitTests 108,
  IntegrationTests 207 + 1 failed): `ConnectorSendDeadlineTests.An_emergency_behind_a_busy_but_healthy_bridge_says_busy_and_does_not_drop_it`
  — "Assert.ThrowsAny() Failure: No exception was thrown" (ConnectorSendDeadlineTests.cs:413). Re-run in isolation
  three times: red, red, red (≈0.8 s each) — deterministic, not a flake. The total 391 = 360 (U2a tip) + 31 (U2b on
  main), so the count is right and one behaviour changed.
- **Decision:** insert round 4b — an Opus BUILDER diagnoses (bisect against `5c716aa` in `u2a-orig`) and fixes on the
  probe branch before ANY verifier or Codex touches the unit; verifying a red sha wastes both legs. The branch ref
  `u2a-pipe-hardening` stays at `5c716aa` until the probe branch is green. Codex waits for the green sha (a review of
  a sha that will change is a review to redo). Brief: `briefs/U2a-r4b-build.md`.
- 17:55 Concurrency: U14 builder (Opus) + U2a round-4b builder (Opus) = 2 heavy legs. The U2a verifier waits.
