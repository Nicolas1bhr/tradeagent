# U-ledger-rebase — carry the finished ledger unit over the landed life loop

Fresh fixer on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/briefs/U-ledger.md` INCLUDING its `## Report`,
and the `U-life` section at the end of `BUILD-STATUS.md`. `export PATH="$HOME/.dotnet:$PATH"
DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. The worktree `~/Projects/ai-trading-software-for-mihael-worktrees/ledger`
already exists on branch `u-ledger` at `ab0c4e9`, clean, 8 commits ahead of the `main` it was built on. `main` has since
landed `U-life` (the mission loop, the AI card in `DashboardView.cs`, a new guide section). You write no new product
behaviour: you carry the branch over.

1. **Rebase** `u-ledger` onto `main` in that worktree. `git merge-tree` says two files conflict: `DashboardView.cs`
   (U-life rewrote the AI card; U-ledger inserted a field, a layout entry and an update call for `PerformanceCard`)
   and `docs/USER-GUIDE.md` (both added a section). Keep BOTH sides' behaviour in each: the AI card as `main` has it,
   the Performance card wired exactly as the ledger's commits wired it, both guide sections. Resolve inside the
   rebase so each original commit keeps its message; add no commit of your own except the report.
2. **Gate** at the rebased tip, as the brief states: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0
   warnings; the four ledger test classes and the four life test classes 3× each; the full suite once in Release to a
   file, one project at a time, with no other test host running (`pgrep -fl 'testhost.dll' | grep -v pgrep`); names vs
   `main` → 0 removed. Two units each green alone can be red together: a failure here is reported, not patched away.
3. **Append** ≤6 lines under the existing `## Report` in `docs/briefs/U-ledger.md`: the rebased tip sha, what the two
   conflicts were and how each was resolved, the gate counts. Commit it. No trailers. Do NOT push, merge, or remove
   the worktree.

Your final message to the manager is those lines and the tip sha, nothing else.
