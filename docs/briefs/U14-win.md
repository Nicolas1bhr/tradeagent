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
- The set varies run to run: run 33907331267 also failed `CoidWitnessTests.The_file_is_never_absent_while_it_is_being_rewritten`
  ("the temporary file was left behind" — the load-dependent race U14 was opened for). All pass on ubuntu, macos and
  this Mac; the U14 record says the cross-process lock was proven on APFS only. For the third test the durability
  count is the property; a leftover temp on Windows may instead be asserted as "reported and renamed out of the glob at
  the next start" if that is what the code does (round 3) — say which in the report.

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

## Report

Tip `f136a1b` on `u14-win`; PR #2, draft, not merged, not rebased (`main`'s new `a4af744` is BUILD-STATUS.md only). Three harness fixes, NO product change, no `Skip`.
1. **Rotating writer.** The harness appended with `File.AppendAllText` (shares READ only) while readers read the same sidecar
with `File.ReadAllLines` (also READ only): a sharing violation on Windows, unarbitrated on APFS. It now opens `FileMode.Append`
sharing `ReadWrite | Delete` and waits a holder out, as the product's append does. Mutant, `Standing` always `Clean`: RED, 0 vs 51.
2. **Missing bridge directory.** Windows refuses `Directory.Move` on a tree holding an open handle and the lease
(`FileShare.None`, instance-lifetime) is one; disposing first loses the point, since `Submitting` leases BEFORE it reads and a
re-lease into a gone folder answers "another writer owns this witness". The directory is now a removable link — junction on
Windows (no privilege), symlink elsewhere. Mutant `catch (DirectoryNotFoundException) { failed = false; }`: RED, "changed underneath".
3. **Churn, the varying one.** `Expected 301 Actual 300` at line 467, NOT a leftover temp: `Submit` threw `Submitting`'s answer
away, so a rename refused for its whole budget — order refused, claim rolled back, the mechanism WORKING — read as a lost record.
It now asserts the promise: the seed plus exactly the accepted claims, temps <= refusals. Two whole-budget refusals injected at
`replace`: new form green, old form RED `301 vs 299`. Mutant, a record dropped after `Save` returned true: RED, `301 vs 300`.

Local at the tip, Release: 0 warnings; each test 3x green; both witness classes 191/191; suite 795 passed, 0 failed.
CI 33924375698 @`f136a1b`: ubuntu SUCCESS, macos SUCCESS, windows FAILURE 518/519 — and the windows trx says all THREE tests Passed.
NOT done: no product change. Windows' one remaining red is `GatewayPipeBackpressureTests.A_close_all_wave_that_disposal_lands_in_
leaves_nothing_unsettled` (`0 vs 1`, line 1308); at `b4cb122` it was `AtasProtocolTests.Capabilities_and_accounts_come_from_the_
bridge_handshake` instead — a different windows test each run, new, not in this brief, NOT investigated.
