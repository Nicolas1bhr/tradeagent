# U-crlf-strategy-win — the day-one fixtures are checked out with CRLF on windows-latest, and the test normalises only one side
Read `docs/HOW-WE-BUILD.md` (step 6, "runner red"; "The fresh-fixer rule"), `CLAUDE.md`, the `## 2026-09-07 — U-crlf-win landed` and
`## 2026-09-12 — U-runner-1 landed` sections of `BUILD-STATUS.md`, `.gitattributes`, then `tests/TradeAgent.UnitTests/Strategies/
DayOneStrategyTests.cs:30-70,180-195`. Branch `u-crlf-strategy-win`, worktree `~/Projects/ai-trading-software-for-mihael-worktrees/
U-crlf-strategy-win`, rebased onto `main` first. Test- and attributes-only; no product code; no assertion loosened; no box, no money.

**The red.** CI run 34719212649 at `1e92fe3` (the `U-runner-1` merge), windows-latest only, ubuntu and macos green; three
theory cases of ONE test, `DayOneStrategyTests.Each_fixture_is_the_program_the_document_prints` (`ma-crossover`, `opening-range-
breakout`, `rsi-mean-reversion`): `Assert.Contains() Failure: Sub-string not found / String: "# The strategy language, v1…" / Not
found: "`opening-range-breakout.strategy`\n```\n# A"…`. Fault 272/272 and Integration 539/540 green on that runner. The cause is the
`U-crlf-win` shape: `.gitattributes` pins `*.md` (the document) to LF but says nothing about `*.strategy`, so `* text=auto` hands
the hosted runner's `core.autocrlf=true` checkout CRLF fixtures; the test normalises the DOCUMENT (`doc.ReplaceLineEndings("\n")`,
line 67) but reads the FIXTURE raw (`Fixture(name).TrimEnd('\n')`), and line 185 splits it on `'\n'` leaving a `\r` on every line.
`git ls-files --eol` shows `attr/text=auto` on the three fixtures against `attr/text eol=lf` on the document.

1. **The class fix, both halves, as `U-crlf-win` did it:** `.gitattributes` gains `*.strategy text eol=lf` in the "source and
   anything the product's text or the build reads" block; and the test normalises the fixture it reads (`ReplaceLineEndings("\n")`
   in `Fixture(name)`, so lines 67 and 185 hold on either checkout), every assertion otherwise byte-identical. RED first, on this
   Mac: convert the three fixtures to CRLF in the working tree (`sed -i '' 's/$/\r/'`, or `unix2dos`) and run the test → the three
   cases red with the CI's message; then the fix → green with the CRLF files still in place; restore the files. Mutant (the
   normalisation removed from `Fixture` again, CRLF files in place) → red. No `Timing` membership: no clock is involved.
2. **Prove it on the runner:** push the branch, open a DRAFT PR, quote the run id with the windows job green on this test, close the PR.
3. **Sweep:** `git grep -n "ReadAllText\|ReadLines" tests/` for any other test that reads a tracked non-`.cs`, non-`.md` file and
   compares it to a literal or to another file; name each as fixed or not at risk and why.

Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the Unit project 3× and the three test projects in
Release to a file → 0 failed; names vs `main` → nothing removed; the draft PR's three runners green (run id quoted). Commit per
item, one sentence, no trailers. Append `## Report` (≤20 lines): tip sha, the gate counts, the RED and mutant quoted, the run id,
the sweep's verdicts, what you did NOT do. Close the draft PR after the report.
