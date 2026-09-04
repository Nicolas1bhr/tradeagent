# U2d — updater fail-closed: the last three items

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, then `docs/hardening/records/U2d.md` for what the
branch already does. Nothing else. Toolchain: `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; there is
no `timeout` binary; the full suite takes 3–6 min, run it to a file. The Windows box is not yours; `UpdateSources.Install`
stays unexecuted — say so in the report.

Worktree `~/Projects/ai-trading-software-for-mihael-worktrees/u2d-build`, branch `u2d-updater-fail-closed` @ `c519966`.
First `git rebase main` (it dry-ran clean on 2026-09-03; if it conflicts, resolve keeping both sides' behaviour), then
`dotnet build TradeAgent.sln --no-incremental` (0 warnings) + the full suite as your baseline.

Yours: `UpdateService`, `ChecksumManifest`, `Downloader`, `ReleaseFeed`, `UpdateSources`, the coupling seam in
`TradeAgent.Diagnostics`, the update banner and Settings surfaces that render refusals, and their tests. Not yours:
`TradingGateway.cs` beyond the existing `InstallInProgress` latch hunk, `AppHost.cs` beyond one wiring line. If an item
needs more than that, stop that item and say so in the report.

1. **Refusal-path `Activity` sink.** A throwing sink on a refusal replaces the owner's reason today. Wrap the refusal
   path the way the success path is wrapped. Test RED first: the refusal reason survives a throwing sink.
2. **A real test through `UpdateSources.GitHub` → `TryGetSmallTextAsync`** with a per-request `HttpListener`:
   declared-oversized refused unopened; chunked past the cap refused at maxBytes+1; a stalling body cut at the leash; a
   healthy manifest resolves within the timeout. Then quote each of three reverts RED against it: unbounded
   `TryGetStringAsync`; no leash; no Content-Length check.
3. **Batch.** Pin the astral-character refusal and the Surrogate/PrivateUse/Unassigned categories in a test; let caller
   cancellation propagate as cancellation; guard `ReadLimitedAsync(_, int.MaxValue)` against overflow; keep the one-line
   `Attach` source assertion with its disclosed limit; rename `UpdateGatewayCoupling` for what it does, every reference.
4. **Not in this unit:** item 10 (the provider counts every wire-touched record through U2c-1's store query) waits for
   U2c-1. Leave the doc comment at `UpdateService.cs:255-265` as it is and list it under NOT done.

Every fix: test RED before, GREEN after, one mutant of the guard watched to go red and quoted (commit before mutating,
restore with `cp`, `touch`). Sweep every caller of `TryGetSmallTextAsync`, `ReadLimitedAsync`, the renamed type and the
sink. Commit per item, one-sentence messages, no `Co-Authored-By` trailers, no push, no other worktree.

Gate at the end: `dotnet build TradeAgent.sln --no-incremental` → 0 warnings; full suite → 0 failed; paste the counts.

## Report

Append here as you go and commit it with each item, at most 20 lines in total: tip sha; rebase result; baseline
counts; one line per item (RED → GREEN → mutant); final counts; what you did NOT do. Every line is verified by running
something, or says NOT VERIFIED. Not allowed: should work, looks correct, probably, I believe, minor, trivial.

Tip **bd5e390** (this report sits on top of it). Rebase onto b5a439d: clean. `main` then moved to **6138fdd** (the u2a-pipe-hardening merge, two
minutes later) and I rebased again — 15/15 replayed, no conflict. Baseline on b5a439d: 0 warnings, 75/182/146 = **403 passed, 0 failed**. `main` has since moved on again to ce40225, docs-only: zero file overlap with this branch, so the landing rebase is conflict-free and changes no code — verified by `comm -12` on the two name-only diffs.

1. Refusal sink. RED (3 tests): `InvalidOperationException: database is locked` thrown out of `InstallAsync` at UpdateService.cs:522, and the background
   check's `Refused` back to False. Fixed with one wrapped `Record(text, level)` that every sink call site now goes through — GREEN 77/77. Mutant (its
   catch removed): 4 red, including the pre-existing post-Launch test. Restored 77/77.
2. Wire test, per-request `HttpListener`, through `UpdateSources.GitHub`: a declared 65537 refused with 1 body byte sent and the other 65536 never coming;
   chunked 65537 refused with the server then silent for good; chunked 65536 read whole; a stalled body cut at a 500 ms leash; a healthy manifest resolving
   to the installer's hash — 5 tests, 0.6 s. Each revert quoted RED against them: unbounded `TryGetStringAsync` → 3 red (two at the test's own 15 s
   deadline, plus the source assertion); no `CancelAfter` → the stall red; no Content-Length check → the declared-size red. Restored 82/82.
3. Batch. Astral U+1F600, PrivateUse U+E000, Unassigned U+0378 pinned, each row asserting its category first (a pin: no RED, the mutant is its proof).
   `ReadLimitedAsync(_, int.MaxValue)` RED `OverflowException` → the +1 is not taken there, and a negative limit throws instead of returning null. Caller
   cancellation RED `No exception was thrown` → rethrown under `when (ct.IsCancellationRequested)`, leash expiry still null. `Attach` source assertion kept with
   its limit disclosed. `UpdateGatewayCoupling` → `UpdateTradingInterlock`: file, class, AppHost line, 6 test refs, 0 left in src/ or tests/. Mutant (the three categories dropped from `IsPlainFileName`): 3 rows red. Restored 89/89.

Final on bd5e390, tree clean: Debug and Release `--no-incremental` both 0 warnings, both suites 75/197/314 = **586 passed, 0 failed**. UnitTests 182 → 197
is exactly the 15 cases these commits add; the integration jump is the u2a merge. NOT done: item 10 — `UpdateService.cs:255-265` verified byte-identical to
the branch base by diff, it waits for U2c-1; `UpdateSources.Install` and every UI surface are executed by no test, and the box was not mine; `docs/hardening/`
still says `UpdateGatewayCoupling`, left as frozen history; `UpdateTests.cs` lost `A_release_without_a_checksum_file_still_installs_without_inventing_one`
in df9b068 — round 1, not mine: it pinned the checksumless install this unit refuses.
