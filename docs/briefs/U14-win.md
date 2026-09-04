# U14-win — two witness tests fail on the Windows runner because their setup does not survive Windows file semantics

Fresh fixer on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, then only the two tests below and the helpers they call.
`export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. Fresh worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/u14-win` on a new branch `u14-win` from `main`. No box; you have
no Windows machine — CI on your draft PR is your only Windows run, so think before each push.

**What happened.** CI run 33904745608 at `2c6826d` (U14a's merge), windows-latest, 503/505: both failures are
exceptions thrown by the TEST's own setup, not assertions about the product.
- `WitnessSnapshotTests.No_reader_reports_a_clean_machine_while_a_writer_is_rotating_under_it`: `IOException: The
  process cannot access the file 'C:\Users\…\Temp\tradeagent…'` from `File.AppendAllText` inside the test's writer
  lambda (`<>c__DisplayClass35_0`) — the harness's writer appends while the product's reader or rotator holds the file.
- `CoidWitnessTests.A_missing_bridge_directory_is_unreadable_rather_than_absent`: `IOException: Access to the path
  '…' is denied` from `Directory.Move` in the test — moving the bridge directory away to simulate "missing" is refused
  while the witness holds a handle inside it (the lock file or an open sidecar).
- The same two pass on ubuntu, macos and this Mac. The U14 record already says the cross-process lock was proven on
  APFS only and Windows sharing violations are injected at the seam.

**Rules.** The product is not changed unless you find it wrong on Windows — then say so first and fix red-first with a
mutant. What each test PROVES must survive: no reader reports a clean machine during a rotation; a missing bridge
directory reads as `Unreadable`, never as "no files". Make the setup reach that state in a way Windows allows: the
harness writer opens with `FileShare.ReadWrite | FileShare.Delete` (or uses the product's own append path); "missing
directory" is produced by constructing the witness against a path that never existed, or by disposing the witness
before the move, rather than moving a directory with open handles. A test that cannot reach its precondition on
Windows says so with `Skip` and the reason in the summary comment — last resort, and the report says why.

**Proof.** Both tests 3× locally in Release; the two classes once; the full suite once in Release; push `u14-win` and
open a draft PR; paste the three CI job conclusions — windows must be green on both tests. Commit per test, one-sentence
messages, no trailers, no other worktree.

## Report — append as you go, commit it, ≤20 lines: tip sha; per test what the harness did on Windows and what you
changed; local counts; CI per platform; what you did NOT do. Verified or NOT VERIFIED.
