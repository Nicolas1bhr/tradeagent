# U-fix-inbox-boundary — the owner's dropped file reads `InboxUnattested` on hosted ubuntu in TWO tests of one property, four times: find the shared cause; if a real machine can do it, fix the product
**Protects:** the inbox's provenance — `CLAUDE.md` ("measurement and claim in different tables"; the `material` row is the scanner's measurement, written
ONCE); `docs/COUNCIL.md` rule 7 (the scanner attests only across proven quiescence). An owner's file misclassed `InboxUnattested` is permanent and says "the AI
was running when it appeared — TradeAgent cannot say who put it there" (`InboxView.cs:285`). **A FRESH FIXER, seat P's slot 1, dispatched first** (the
orchestrator, 2026-10-02: three of the last five `main` runs red on it; seat A lands under a judged exception for these two tests until this unit lands).
**Evidence (RUN, `gh run view`, 2026-10-02), each "Assert.Equal() Failure: Values differ / Expected: Inbox / Actual: InboxUnattested", ubuntu-latest only,
windows-latest and macos-latest green in the same run:** `CouncilLoopTests.A_file_dropped_between_turns_is_still_the_owners_when_another_role_tries_to_launch`
(`tests/TradeAgent.UnitTests/CouncilLoopTests.cs:726`, assert `:769`) — run 36891262990 at `22b424d` (a branch, green on its retry, `BUILD-STATUS.md:6840`),
run 37024865144 at `main` `a9c10c6` (job 110896796456, 106 ms), run 37030431278 at `main` `926be6e` (job 110915581905, 178 ms);
`MissionLoopTests.A_file_the_owner_drops_between_turns_is_still_recorded_as_theirs` (`MissionLoopTests.cs:219`, assert `:232`) — run 37030558283 at `main`
`4ce671f` (job 110916005676, 122 ms). `926be6e` and `4ce671f` are docs-only commits on the build tree that was green at `c8d6642` and `ace127f`.
**The property's other tests (keep them green, weaken none):** `QuiescenceBarrierTests.The_same_file_with_no_chat_turn_running_is_the_owners` (`:131`);
`InboxWakeTests.A_file_dropped_in_the_inbox_still_raises_one_wake` (`:127`); `MaterialOriginAttestationTests` `:46`, `:80`, `:161`.
**Facts to start from (SOURCE at `4ce671f`; facts, not a diagnosis):** a pass's window starts at the previous complete pass's `now`, a `DateTimeOffset.UtcNow`
read at that pass's start and stored at its end (`MaterialScanner.cs:129, 136, 227`); the inbox's word is asked per sighting (`:184`); an agent process's exit
stamps `_lastAliveAt = DateTimeOffset.UtcNow` under the presence lock (`AgentPresence.cs`, `Window.Dispose`), and `NoneSince` is strict — `_live == 0 &&
(_lastAliveAt is null || _lastAliveAt < since)`, "unsure is false" — so both sides of the comparison are wall-clock reads. The loop's two yields: the pass AFTER
a turn moves the window's start past that turn's last breath (`MissionLoop.cs:1738-1750`); the pass BEFORE the next turn records the file (`:1555-1564`);
`PassAsync` refuses while any role turns and leases refuse while it passes (`:1222-1246`); `MissionLoopTests.cs:208-217` says why both are needed. The product's
presence window wraps each agent process (`CliAgentRuntime.cs:198`); the tests' hosts stand in for it (`CouncilLoopTests.cs:60-100, 190-219`;
`MissionLoopTests.cs:30, 96, 159`).
**THE PRODUCT QUESTION, answered with evidence:** on a real machine, can a file that arrives at a turn boundary — just after a turn's process exits, or while a
second role launches beside the pass — be classed `InboxUnattested` although no agent was alive after it arrived? If YES, the fix is in the PRODUCT, with a
red-first test that is DETERMINISTIC (drive the boundary through seams or an injected clock; no sleeps, no loops that wait for luck), and neither red test may go
green by widening what counts as owner material. If the cause is only the tests' harness, say exactly where it differs from the product's path and why "a file
the owner drops between turns is recorded as theirs" is still proven by the product's own path. One cause for both tests, or each named separately.
**Reproduce on CI, not on this Mac** (green here proves nothing about ubuntu): `fleet/bin/ci-dispatch.sh`, read the ubuntu job. Diagnostic runs may narrow
`.github/workflows/build.yml` on your branch (ubuntu only, the classes looped) and print timestamps (`since`, `LastAliveAt`, each pass's `now`, the file's
write time) — branch only, removed before the proving run, each named in the report. The Windows box is NOT granted.
Read first: `CLAUDE.md`; `docs/HOW-WE-BUILD.md`; the seven tests and their hosts; `AgentPresence.cs`; `MaterialScanner.cs`; `MissionLoop.cs:1222-1260, 1540-1760`.
Must NOT: add a retry; add a sleep or raise a timeout until it passes; move a test into `Timing` (none asserts a deadline); skip on Linux; weaken an assert;
make `NoneSince` say yes when unsure, or attest a sighting whose window held a live agent — the over-inclusive direction stays (`AgentPresence.Enter`'s summary).
Items, one commit each, one-sentence messages:
1. Name the cause with evidence from an ubuntu run: the values on both sides of the failing comparison, and which pass recorded each row.
2. Fix it where it is wrong: product (red-first, deterministic, one watched mutant of the guard quoted) or harness (the reason above), naming the protected
   property and how the change keeps it.
Done: the red-first test red before and green after (quoted); the mutant red (quoted); the full workflow green on all three platforms on the final tip (run id);
any narrowed stress run quoted with its count; the gate. Gate and report per `docs/HOW-WE-BUILD.md` and `docs/FLEET.md` "The builder pass": rebase on `main`
first; `--no-incremental` Release 0 warnings; Unit and Fault 0 failed; touched classes 3×; names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20
lines appended here. No push to `main`, no merge; touch nothing in `docs/briefs/` but this file.
