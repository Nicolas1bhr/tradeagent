# U-typed-catalog — a unit test that reads the runtime override file races the test that corrupts it

Fresh fixer on Opus. Read `docs/HOW-WE-BUILD.md` (step 6: a red on a hosted runner in a test the Mac and Windows pass is
a fixer on top of `main`), the `U-batch-2b` and `U-life` sections of `BUILD-STATUS.md`, `RuntimeManifest.cs`
(`RuntimeCatalog.Require`, `VendorFile`), `tests/TradeAgent.UnitTests/TypedWhileWorkingTests.cs`, and the test class
from `U-batch-2b` that deliberately writes a corrupt `runtimes.json` and the xUnit collection it shares with the other
readers. `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. Worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/typed`, branch `u-typed-catalog` from `main`. Test-only unless
the product is the cause; another builder works `U-meter` (`MissionLoop.cs`, `AgentSession.cs`, the AI card, `Settings`)
and you touch none of its files.

**The red**, CI run 34040176577 (ubuntu-latest, docs-only commit `b04e1a3` over the `U-life` tree `0ec96c6`), three
tests of `TypedWhileWorkingTests` in about 1 ms each, all with the same message:
`TradeAgent.Core.TradeAgentException : runtimes.json could not be read, so TradeAgent will not start an AI assistant
and is not falling back to the commands it ships with. The reason: the text in it is not valid JSON.` — thrown at
`RuntimeCatalog.Require(String id)` (`RuntimeManifest.cs:448`) from `TypedWhileWorkingTests.Session()` (line 24). The
same tree passed at `b3e7d0a` (run 34040307786) and on this Mac: the class reads the override file from the shared
test home while the corruption test has it corrupted, and it is not in that test's collection.

1. Make `TypedWhileWorkingTests` independent of the disk: build its `AgentSession` from a manifest in memory
   (`RuntimeCatalog.BuiltIn()` or a literal `RuntimeManifest`), never from the override file. Then sweep the other
   `U-life` classes (`MissionLoopTests`, `MissionControlsTests`, `MissionInstructionsTests`) and every other unit test
   for a read of `runtimes.json` or `atas.json` outside the shared collection; each such read is fixed the same way or
   joins the collection, with the choice argued in one line each. RED first: reproduce the race deliberately (the
   corruption test's write, then `Session()`) → the message above; GREEN after; the reproduction stays as a test.
2. Gate: Release `--no-incremental` → 0 warnings; the touched classes 3×; the full Unit suite 5× (the race is in
   parallel ordering); the full suite once to a file, one project at a time, nothing else running; names vs `main` 0
   removed. Open a DRAFT PR from the branch so all three hosted runners run it; quote the three results.

## Report — append here, commit it, ≤12 lines: tip sha; the reproduction's RED and GREEN; the sweep's findings; the
gate counts and the three runner results. Verified or NOT VERIFIED, nothing in between. No push except the draft PR.

**Tip `b0bb35b`** (2 commits over `f2e30f0`, both test-only; PR #12, draft, not merged).

- **RED, deliberately** (verified, `dotnet test --filter TypedWhileWorkingSurvivesACorruptRuntimesFileTests`): the new
  test corrupts `runtimes.json` then builds the session → `TradeAgentException : runtimes.json could not be read … the
  text in it is not valid JSON`, at `RuntimeManifest.cs:448` from `TypedWhileWorkingTests.Session()` — the CI message,
  file and frame exactly. **GREEN** after `Session()` took its manifest from `RuntimeCatalog.BuiltIn()` (a literal, no
  file): 5/5 in that file. The reproduction stays, in the collection, because it writes the file.
- **The sweep** (verified by instrumenting `VendorFile.Read` to log a stack per call and running the whole assembly):
  28 vendor-file reads, every one inside `[Collection(VendorOverrideFiles.Name)]` — `VendorOverrideFileTests` 17,
  `RuntimeCatalogTests` 4, `DoctorReconciliationCheckTests` 2 (+4 `Doctor.RunAsync` continuations past an await, whose
  only callers in this assembly are those two classes), the new reproduction 1. No writer outside those classes; the
  other two assemblies get their own `TestEnv.Home` and write neither file, so they cannot race. Nothing else to fix, so
  the sweep is asserted instead of reported: a source scan fails naming file, class and call. Mutant (`Require` put
  back) → red, `TypedWhileWorkingTests.cs: TypedWhileWorkingTests calls RuntimeCatalog.Require(`.
- **Gate at `b0bb35b`, nothing else running** (verified): Release `--no-incremental` → 0 warnings, 0 errors; touched
  classes 3× → 14/14 each; Unit suite 5× → 329/329 each; full suite one project at a time → 329 + 261 + 610 = 1200
  passed, 0 failed, 1 skipped; names vs the merge-base → **0 removed**, 2 added (327 → 329).
- **The three runners** (verified, CI run 34041875509 at `b0bb35b`): ubuntu-latest **pass** (11m33s; the assembly that
  was red is 329/329), macos-latest **pass** (14m42s), windows-latest **pass** (16m1s), `package` **pass**.
- `main` has moved to `c1a8ee2`, whose own note records this same race red on macos. Verified from `main`'s tree: none
  of the unit test files it has gained reads a vendor file, so the guard holds over the merge. NOT VERIFIED: no suite
  has been run on the merge of this branch with `c1a8ee2`.
