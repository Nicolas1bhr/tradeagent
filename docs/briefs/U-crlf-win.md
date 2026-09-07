# U-crlf-win — a raw-string paragraph compared line by line survives a CRLF checkout
Read `docs/HOW-WE-BUILD.md` (step 6 of the landing checklist), `CLAUDE.md`. Branch `u-crlf-win`, worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/U-crlf-win`, rebased onto `main` first. Test-only plus one repository
attribute file; no product code; no box, no ATAS, no money.

**The red.** CI run 34073713557 at `06a8636` (the `U-seen-1` merge), windows-latest only, macos and ubuntu green:
`TradeAgent.Tests.Unit.MissionInstructionsTests.The_paragraph_is_the_only_difference_between_the_two_missions` →
`Assert.All() Failure: 13 out of 13 items in the collection did not pass`, every item `Assert.Contains() Failure: Item
not found in collection`, e.g. `"**This platform is TradeAgent's built-in simulator"···`. The test
(`MissionInstructionsTests.cs:172-179`) splits both missions on `'\n'` and asserts each added line is in
`SimulatorLines`; `WorkspaceBuilder.SimulatorParagraph` (`WorkspaceBuilder.cs:121`) is a `"""` raw string literal, so on a
checkout where git wrote `.cs` files with CRLF (the hosted Windows runner's default `core.autocrlf`) every line of the
paragraph ends in `\r` and matches nothing. The repository has NO `.gitattributes`. The same tree passed 454/454 on this Mac.

1. **The class fix, at the root:** a `.gitattributes` at the repository root: `* text=auto` and `*.cs text eol=lf` (add
   `*.md`, `*.json`, `*.yml`, `*.sh` with `eol=lf`; `*.cmd`/`*.ps1` `eol=crlf`; binaries `-text` as found). Renormalise
   (`git add --renormalize .`) and report what changed — expected nothing on this Mac, since the files are LF already. Say
   in the report why this is the class fix: every raw string literal in the product now compiles identically on every
   checkout, so the mission text, the schema text and the guide the app writes carry the same bytes on Windows.
2. **The test made honest on its own:** split on both endings (`Split(["\r\n", "\n"], StringSplitOptions.None)` or trim a
   trailing `'\r'`) so the assertion holds on a CRLF checkout even without item 1; the assertion itself is unchanged — it
   still names every line. RED first: reproduce the runner's condition locally by rewriting the paragraph's line endings
   to CRLF inside the test (a copy of the literal with `\r\n`, or a temporary `git config core.autocrlf true` checkout in
   a scratch worktree — say which); quote the 13-of-13 failure; then GREEN. Mutant: the `\r` handling removed → RED again.
3. **The sweep, reported not assumed:** `git grep -n "Split('\\\\n')\|Split(\"\\\\n\")" -- 'tests/*.cs' 'src/*.cs'` — list
   every site that splits text on a bare `'\n'` and compares to a raw literal or a source-text line; fix only those with
   the same failure shape (one line each, same pattern as item 2), name the rest as not at risk and why.

Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the Unit project 3× and the three test
projects in Release to a file → 0 failed; test names vs `main` → nothing removed. Commit per item, one sentence each, no
trailers. Append `## Report` (≤20 lines): tip sha, the gate counts pasted, the RED and mutant output quoted, the sweep's
list, the renormalise output, and what you did NOT do.

## Report
Code tip `e3b0474`; this report is the commit above it. `dotnet build TradeAgent.sln -c Release --no-incremental` → `Build
succeeded. / 0 Warning(s) / 0 Error(s)`, 17 projects, 37 `CoreCompile:` tasks. Unit ×3 → `Failed: 0, Passed: 454, Skipped: 0,
Total: 454` each; Fault → `Failed: 0, Passed: 269, Skipped: 0, Total: 269`; Integration → `Failed: 0, Passed: 615, Skipped: 1,
Total: 616`. `--list-tests`, branch vs `main` built in scratch: 1339 = 1339, `comm` empty both ways — nothing removed.
1. Root `.gitattributes`: `* text=auto`; `eol=lf` on `.cs .csproj .props .sln .slnx .md .json .yml .yaml .sh .py .txt`; `eol=crlf` on
`.cmd .bat .ps1`; `-text` on twelve binary extensions, none tracked. `git add --renormalize .` → `M docs/atas-api-8.0.14.397.txt`, the
only tracked file carrying CR (5,882 lines), and `git diff --cached --ignore-cr-at-eol` on it is empty: endings alone. The ROOT fix —
no `.csproj` declares an `EmbeddedResource`, so every shipped word is a `"""` literal in a `.cs`, which keeps its SOURCE FILE's endings.
2. RED by `git archive HEAD | tar -x` to a scratch tree with all 159 `.cs` rewritten to CRLF by `perl -pe 's/(?<!\r)\n/\r\n/'`,
byte-for-byte what `core.autocrlf=true` hands the runner; no git config touched, this worktree never held a CR. Unfixed →
`Assert.All() Failure: 13 out of 13 items in the collection did not pass.`, `[4]: Item: "report one as though it did.\r"` — the CI
failure. Fixed → `Passed: 454, Failed: 0`, same tree. Mutant (`Lines()` cut to `text.Split('\n')`) → the same 13-of-13 failure.
3. Sweep, 14 sites (12 from the brief's grep, 2 from a widened one). FIXED 1: `MissionInstructionsTests.cs:177`. NOT AT RISK 13,
run under CRLF rather than argued — normalise first: `MissionLoop.cs:193,616`, `TurnMeterTests.cs:101,120`; `Trim()`/`TrimEntries`
eats the `\r`: `CliAgentRuntime.cs:194,424,537`, `NodeRuntime.cs:161`, `UpdateService.cs:958`, `GatewayPipeBackpressureTests.cs:1175`
(reads a `.cs` off disk); substring assertions only, which `\r` cannot move: `EmptyAllowlistTests.cs:91,92`, `CoidWitnessTests.cs:
1730`; no assertion: `BridgeRoundTripTests.cs:584`. Evidence: CRLF tree Unit 454/454, Fault 269/269, those 2 integration tests 2/2.
NOT DONE: no product code, no Windows box, no ATAS, no money, no CI run observed. `EmptyAllowlistTests.cs:91,92` left on a bare
`'\n'` — different failure shape, as the brief directs. The vendor dump renormalised rather than marked `-text`.
