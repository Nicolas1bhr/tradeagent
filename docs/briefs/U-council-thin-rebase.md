# U-council-thin-rebase — rebase the finished council branch over the landed dataset unit and renumber its schema to 9
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, the `## Report` at the end of `docs/briefs/U-council-thin.md`, the `U-data-binance
landed` section of `BUILD-STATUS.md`. Branch `u-council-thin` in worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/U-council-thin`. One item; no new behaviour; the commits keep their
messages; no box, no ATAS, no money.

**Why.** `U-council-thin` (tip `119990c`, gate green: 529 + 277 + 615, 19 names added, 0 removed) took schema 8 while
`U-data-binance` (now on `main`) also took 8 and landed first. `git rebase main` will conflict in `Database.cs`,
`Versioning.cs`, `docs/CONTRACTS.md`, the schema tests and possibly `MissionLoop.cs`, `AppHost.cs`, `WorkspaceBuilder.cs`,
`Trading.cs`, `GatewaySchema.cs`, `MissionInstructionsTests.cs` (both units touched them). A conflict goes to a builder, not
the manager (checklist step 2).

1. `git -C <worktree> rebase main`, resolving every conflict INSIDE the rebase so each commit keeps its message: both
   migrations kept in order (`dataset` at 8 from `main`, the council's tables renumbered to 9: `Database.cs`,
   `Versioning.cs` prose and constant, `docs/CONTRACTS.md`, the exact-version pin in the council's schema test; the
   `dataset` pin at 8 untouched); both sides of every shared file kept by name (their Situation line and the roles'
   sections; their `data/` sentences and the role sections in the mission; their Settings section and the two Safety
   rows). Nothing of `main`'s removed: `git diff main -- <file>` must show only additions for every shared file except
   where the council deliberately replaced a single-role line — name each such line in the report.
2. Gate on the rebased tip: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects
   in Release to a file → 0 failed; the council's 21 touched classes and the dataset's classes 3×; test names vs `main` →
   nothing removed. A `Timing` red re-run alone 3×, never loosened. Another leg may be gating on this Mac at the same time.
3. Append to the existing `## Report` of `docs/briefs/U-council-thin.md` (keeping it ≤ 20 lines in total — trim the old
   lines rather than exceed): the rebased tip, the gate counts, the conflicts resolved by file, the deliberately replaced
   lines, and the schema number. Commit it, one sentence, no trailers.
