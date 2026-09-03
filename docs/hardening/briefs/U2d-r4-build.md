# U2d — BUILD BRIEF · round 4, items 1–3 (item 10 follows U2c-1's merge)

**Tier T2 with a T1 consequence** (the updater replaces the program holding the owner's open orders). **Legs:** you are
leg [1]; an Opus verifier [2] runs on your final sha; Codex [3] on trigger (the manager decides). **Round cap: 2** for
this batch (round 3's verdict was 0H/2M/4L; you close the two MED and the LOW batch).

**FIRST, in this session, read in full:**
1. `/Users/nicolasbeeckman/Projects/innovision-os/innovision-os/docs/ORCHESTRATION-STANDARD.md` (mandatory read-gate).
2. `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/CLAUDE.md`.
3. `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/docs/hardening/records/U2d.md` — rounds 1–3, the
   verifier's positives worth keeping, and "Round 4 (briefed, NOT started)". Original records lost; branch = truth.
4. `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/docs/hardening/records/commits-u2d-updater-fail-closed.md`.

## Where you work

- Worktree `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael-worktrees/u2d-build`, branch
  `u2d-updater-fail-closed` @ `c519966` (base `3931c10`, 12 commits).
- Toolchain: `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`. No `timeout`; tool calls cap at 10 min;
  full suite ≈ 2–3 min, output to a file. The Windows box is OFFLINE (`UpdateSources.Install` stays unexecuted; say so).

## Step 0 — rebase onto the `main` of the moment, prove the baseline

`git rebase main` on the branch. The manager dry-ran it on 2026-09-03 against `main` `7c94cfe` and against the U2a tip:
clean both times. If it conflicts when you run it (U2c-1 may have landed: `TradingGateway.cs`, `GatewayTypes.cs`,
`AppHost.cs`, `Errors.cs`), resolve keeping both sides' behaviour. `dotnet build TradeAgent.sln` + FULL suite; counts
into the record before round 4.

## Items

1. **Refusal-path `Activity` sink (MED).** Wrap the refusal path like the success path. RED first: a throwing sink on a
   refusal currently replaces the owner's reason (mutant ADV3-I kept 373 green) — the new test must show the refusal
   reason survives a throwing sink, and go RED against the unfixed code.
2. **A real test through `UpdateSources.GitHub` → `TryGetSmallTextAsync` (MED)** with a per-request-dispatch
   `HttpListener`: declared-oversized refused unopened; chunked beyond the cap refused at maxBytes+1; a stalling body cut
   at the leash; a healthy manifest resolves. Then the three reverts (unbounded `TryGetStringAsync`; no leash; no
   Content-Length check) each quoted RED against the new test.
3. **LOW batch:** document/pin the astral-character refusal and the Surrogate/PrivateUse/Unassigned categories; let
   caller cancellation propagate as cancellation; guard `ReadLimitedAsync(_, int.MaxValue)` overflow; keep the one-line
   `Attach` source assertion with its disclosed limit; rename `UpdateGatewayCoupling` for what it does (and every
   reference).
4. **NOT in this batch — item 10** (the provider counts every wire-touched record via U2c-1's store query) waits for
   U2c-1's merge; leave the doc-comment at `UpdateService.cs:255-265` as is and note it under "What I did NOT do".

## Proof obligations

RED quoted before each fix, GREEN after, mutants watched to bite (commit before mutating; `cp` restore; `touch`). Both
directions on item 2: the oversize/stall cases are refused AND a healthy manifest still resolves within the timeout.
R3 sweep: every caller of `TryGetSmallTextAsync`, `ReadLimitedAsync`, the renamed coupling type, and the `Activity`
sink. Gates: targeted per item; `dotnet build TradeAgent.sln` + FULL suite at the end, counts pasted.

## Ownership (R2)

Yours: the updater (`UpdateService`, `ChecksumManifest`, `Downloader`, `ReleaseFeed`, `UpdateSources`), the coupling
seam in `TradeAgent.Diagnostics`, the update banner/Settings surfaces that render refusals, their tests. NOT yours:
`TradingGateway.cs` beyond the existing `InstallInProgress` latch hunk, U2c-1's store query, `AppHost.cs` beyond the
one wiring line you can name. If an item needs more, STOP that item and report.

## Rules

No `Co-Authored-By` trailers; one-sentence commit messages; commit after every item. Checkpoint AS YOU GO by appending
`## Round 4 (build record, <date>)` to `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/docs/hardening/records/U2d.md`
(MAIN worktree path; no git there — the manager commits it). Honesty contract (§6); banned words: should work, looks
correct, probably, I believe, minor, trivial, static-verified, basically. Do not push, merge, or touch other worktrees.
Docs listed under "Docs to change at integration" in the record are NOT yours — the manager batches them.

## Report back

Tip sha; rebase result; baseline counts; per item RED → GREEN → mutant bit in one line each; the R3 sweep; suite counts;
"What I did NOT do".
