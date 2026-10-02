# U-fix-inbox-boundary — the owner's dropped file read `InboxUnattested` on hosted ubuntu, twice: find why, and if a real machine can do it, fix the product
**Protects:** the inbox's provenance — `CLAUDE.md` ("measurement and claim in different tables"; the owner's material is data, and the `material` row is the
scanner's measurement, written ONCE); `docs/COUNCIL.md` rule 7 (the scanner attests only across proven quiescence). An owner's file misclassed
`InboxUnattested` is permanent and says "the AI was running when it appeared — TradeAgent cannot say who put it there" (`InboxView.cs:285`). **A FRESH FIXER,
first in seat P's slot after the 2026-10-02 20:40 CEST reset** (the orchestrator's ranking: provenance and `main`'s CI health first).
**Evidence (RUN, `gh run view`, 2026-10-02):** `CouncilLoopTests.A_file_dropped_between_turns_is_still_the_owners_when_another_role_tries_to_launch`
(`tests/TradeAgent.UnitTests/CouncilLoopTests.cs:726`) failed on ubuntu-latest at `main` `a9c10c6`, run 37024865144, job 110896796456, in 106 ms: "Assert.Equal()
Failure: Values differ / Expected: Inbox / Actual: InboxUnattested" (`:769`, the last line); Unit 1 failed / 1216 passed, Fault and Integration green; windows-latest
and macos-latest green; the seat-P landing gate on the same code green on this Mac. First sighting: run 36891262990 at `22b424d`, ubuntu, the same `Actual:
InboxUnattested`, green on its retry (`BUILD-STATUS.md:6840`). That commit's diff (prices, docs) does not reach this path — a hosted-runner red until shown otherwise.
**Facts to start from (SOURCE at `ace127f`; facts, not a diagnosis):** a pass's window starts at the previous complete pass's `now`, written at its end
(`MaterialScanner.cs:129, 136, 227`), and the inbox's word is asked per sighting (`:184`); `AgentPresence.NoneSince` is strict — `_live == 0 && (_lastAliveAt is
null || _lastAliveAt < since)`, "unsure is false" (`AgentPresence.cs`, its summary and `Window.Dispose`); the loop yields to the scanner before a turn only when
`InboxChangedSinceLastPass` (`MissionLoop.cs:1555-1564`), and `PassAsync` refuses while any role turns and leases refuse while it passes (`:1222-1246`); the
product's presence window wraps each agent process (`CliAgentRuntime.cs:198`); the test's host (`CouncilLoopTests.cs:190-219`: `_lastPassAt` taken before the
walk, `WhileScanning`, `MaterialScanner(_db, Root, Presence.NoneSince)`) and its conversation's window (`:60-100`) stand in for those.
**THE PRODUCT QUESTION, answered with evidence:** on a real machine, can a file that arrives at a turn boundary — just after a turn's process exits, or while a
second role launches beside the pass — be classed `InboxUnattested` when no agent was alive after it arrived? If YES, the fix is in the PRODUCT, with a red-first
test that is DETERMINISTIC (drive the boundary through seams or clocks; no sleeps, no loops that wait for luck), and the existing test must not go green by
widening what counts as owner material. If the cause is only the test's harness (its host or conversation differs from the product's path), say exactly where it
differs and why the property "a file the owner drops between turns is recorded as theirs" is still proven by the product's own path.
**Reproduce on CI, not on this Mac** (green here proves nothing about ubuntu): your branch, `fleet/bin/ci-dispatch.sh`, read the ubuntu job. Diagnostic runs may
narrow `.github/workflows/build.yml` on your branch (ubuntu only, the class looped) and may print timestamps (`since`, `LastAliveAt`, each pass's `now`, the file's
write time) — branch only, removed before the proving run, each named in the report. The Windows box is NOT granted.
Read first: `CLAUDE.md`; `docs/HOW-WE-BUILD.md` (the fresh-fixer rule; the `Timing` category's rule in `build.yml`); `CouncilLoopTests.cs` (the class's host,
conversation and the attestation tests around `:700-775`); `AgentPresence.cs`; `MaterialScanner.cs`; `MissionLoop.cs:1222-1260, 1540-1580`; `MaterialStore.Observe`.
Must NOT: add a retry; add a sleep or raise a timeout until it passes; move the test into `Timing` (it asserts no deadline); skip it on Linux; weaken an assert;
make `NoneSince` say yes when unsure, or attest a sighting whose window held a live agent — the over-inclusive direction stays (`AgentPresence.Enter`'s summary).
Items, one commit each, one-sentence messages:
1. Name the cause with evidence from an ubuntu run: the timestamps on both sides of the failing comparison, and which pass recorded the row.
2. Fix it where it is wrong: product (red-first, deterministic, the watched mutant quoted) or harness (the reason above), naming the protected property and how
   the change keeps it.
Done: the red-first test red before and green after (quoted); one watched mutant of the guard red (quoted); the full workflow green on all three platforms on the
final tip (run id); any narrowed stress run quoted with its count; the gate. Gate and report per `docs/HOW-WE-BUILD.md` and `docs/FLEET.md` "The builder pass":
rebase on `main` first; `--no-incremental` Release 0 warnings; Unit and Fault 0 failed; touched classes 3×; names vs `main` 0 removed (both set sizes printed);
`## Report` ≤ 20 lines appended here. No push to `main`, no merge; touch nothing in `docs/briefs/` but this file.
