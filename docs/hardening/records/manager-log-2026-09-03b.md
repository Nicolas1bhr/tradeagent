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
- 18:05 Rebase dry-runs (probe worktrees, removed afterwards): **U2c-1** `--onto main cb2ce2f` → clean, 21 of 28
  commits survive (the 7 old U2b commits drop out); **U2c-1 onto the U2a tip `e91293e` → CONFLICT in
  `src/TradeAgent.Gateway/GatewayTypes.cs`** (so the U2c-1 builder resolves that after U2a lands); **U2d** onto main →
  clean (12 commits); U2d onto the U2a tip → clean.
- **Pipeline order (slots, ≤2 heavy):** U2a r4b builder → U2a verifier + Codex → integrate U2a · U14 builder → U14
  verifier + Codex → integrate U14 · then U2c-1 round 4 (long pole; rebases over U2a with the known conflict) and U2d
  round 4 items 1–3 (item 10 after U2c-1 merges) as slots free · then v0.1.2 on the box (needs Nicolas: power + Tailscale).
- Briefs on disk for every remaining leg: `briefs/U2c1-r4-build.md`, `U2d-r4-build.md`, `U14-r4-verify.md`,
  `U14-r4-codex-prompt.txt` (plus the U2a pair). A killed leg is re-briefed from these, not from memory.
- 18:25 **U14 builder reported** (Opus, 219K tokens, 18 min): tip `e22eec6`, suite 382 → **387 green** (75/111/201),
  item 6 done. Two REAL gaps it found while pinning: the membership rule's missing-prefix allowance was unconditional
  (a swap-one candidate under the cap was adopted — RED quoted, fixed in `6e9027c`), and `Identified`'s lock check had
  no test (mutant M3b survived every pre-existing test). Docs: RESUME-HERE now expects `proto=3`; CONTRACTS.md carries the
  protocol-3 sentence. Flagged for the verifier: the allowance now reads THIS instance's `_cap`, so a temp from a
  larger-capped build is refused where `a8b3fb0` adopted it (safe direction, unmeasured).
- 18:27 Pinned `u14-verify-r4` and `u14-codex-r4` (detached, `e22eec6`); Codex `gpt-5.6-sol` launched detached on the
  codex worktree, output → `records/codex-U14-r4.txt`; U14 verifier (Opus) dispatched with `briefs/U14-r4-verify.md`.
  Heavy legs: U2a r4b builder + U14 verifier = 2.
- 19:55 **U2a round-4b builder reported** (Opus, 234K tokens, 26 min): the red test was U2a's OWN (red at `5c716aa`
  too) — the fixture assumed 400 × 512 KiB RPCs would still be draining at 2 s; this Mac drained them in ≈1 s and the
  cancel-all took a free gate at 0.71 s. Fix in the TEST only (`ConnectorSendDeadlineTests.cs`, 143+/35−): a
  `BridgePeer.ReadingSlowly` peer (≤ 8 KiB per 200 ms — a wall-clock bound, 12.95 s for the 512 KiB order) and the test
  asserts its own premise before the verdict. Three mutants bite, incl. the OLD fixture under the new assertions ("it was
  never queued behind anything"). Tip **`d25dbb4`**, full suite **391 green** (75/108/208). Lesson for the record: the
  round-4 "360 green" claim never covered this test as written — a claim without the run is not a claim.
- 19:58 `u2a-pipe-hardening` moved `5c716aa` → **`d25dbb4`** (24 commits ahead of main; old tip pinned in `u2a-orig`).
  `u2a-verify-r4` + `u2a-codex-r4` detached at `d25dbb4`; Codex `gpt-5.6-sol` launched on the codex worktree
  (→ `records/codex-U2a-r4.txt`); U2a verifier (Opus) dispatched with the updated `briefs/U2a-r4-verify.md` (target 6 =
  the rewritten fixture is a tooth). Heavy legs: U14 verifier + U2a verifier = 2.
- 20:10 **Nicolas answered the six decisions** in one line: no broker, no account, no SSH to Mihael's machine — do all
  that is possible with what we have; Binance could be connected for testing later (intended use is futures). Recorded
  as resolutions in `HANDOFF-2026-09-03.md` §5. Consequences: the live trial stays out of scope; signing deferred with
  the SmartScreen sentence (D7 by deferral); the test box is the target (D6 walk-through on it); monitoring = A on the
  box (D5); the five keyboard minutes fold into the v0.1.2 box session; containment waits for the U12 revision.
- 20:10 Tailscale up; box reachable (`win-state.sh`: uptime 1d 04h, console session active, ATAS running, TradeAgent
  0.1.1 installed, home present, **UI agent NOT running** — restart with `tools/win-agent.sh start` at the box session).
- 20:35 **Codex on U14 `e22eec6` (gpt-5.6-sol, ~60 min): 6 HIGH / 6 MED / 4 LOW** (`records/codex-U14-r4.txt`). The
  HIGHs cluster into two classes the design already names: ownership is a per-call lock, not a lifetime lease (F1);
  recovery adopts temps it cannot prove legitimate (F3 full replacement at cap, F4 semantically invalid envelope →
  unflagged zero, F8 a refused submission's temp adopted after restart — pinned by a test at line 1800, F13 corrupt
  committed bytes still an anchor); plus F5: operator close-all reaches ATAS without `Submitting` (money path, adapter).
  **Triage:** all six HIGH plausibly real by the record's own design; none refuted from the manager's seat. Round 5
  bounce written with the manager's direction on each class (`briefs/U14-r5-bounce.md`): lifetime lease = an exclusive
  handle released by the OS on death; a temp is never adopted as a NEW claim; a legal transition preserves every committed
  record but at most one trimmed; an unparseable file is not an anchor; close-all witnessed before the wire. The box is
  up, so the adapter hunks (F5/F16) get their first compiler on the box via the win tools. The bounce goes to the SAME
  builder (§9.3) once the U14 verifier vacates its slot; the verifier's findings are appended to the same round.
- 21:05 **U14 verifier reported** (Opus, 274K tokens, 31 min): **FAIL 0H/5M/4L**; behaviours held (3 processes × 240
  claims → 80 durable / 0 lost / 0 phantom, one owner; nine builder mutants reproduced; 387 green twice). Its V1 = Codex
  F7 (RESOLVED rationed), V3 = Codex F2 (readers write) with the class fix "degraded = unresolved SAFETY lines"; V5 says
  the builder's cap-direction argument was backwards (safe direction, wrong record). Findings appended to
  `briefs/U14-r5-bounce.md`; the SAME builder resumed by message (§9.3). Heavy legs: U2a verifier + U14 builder r5 = 2.
