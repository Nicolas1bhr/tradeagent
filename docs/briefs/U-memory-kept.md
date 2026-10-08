# U-memory-kept — a plan or journal the app refuses for size is kept in the archive before the last accepted revision is put back, never destroyed
**Protects:** `docs/PRINCIPLES.md` "Useful autonomy needs code, tools and durable memory" — "a short summary limit is not a reason to discard the underlying research. Prefer a bounded summary
with artifact references, explicit refusal and recoverable output over silent truncation or destruction" — and the role's own memory (COUNCIL's tier iii). Light; seat B (seat P closed, R21); no rung; not the money
path. Lands before M0 attempt 4. From R20 (`docs/research/2026-10-08/R20-autonomy-audit.md` § 1), an outside audit's claim checked at `main` `30fe7a32` and found worse than it said.
**Facts (SOURCE at `30fe7a32`, read and not run).**
- `AgentRuntime/WorkspaceRevisions.cs`: caps `PlanLines` 60 (`:40`), `JournalLines` 200 (`:47`), counted on non-empty lines (`:183-186`); `ArchiveDir` = `trading/archive` (`:50`), which
  "nothing caps and nothing versions" (`WorkspaceBuilder.cs:80-86`). `Snapshot` (`:82-148`) records content only on the valid branch (`:102-118`); a refused file is recorded nowhere —
  the notice holds its line count and the restored revision number (`:120-140`, kv `revision_rejected:<role>:<kind>`, `:188-193`) — and the returned `Restore` (`:71`) is written
  over the file after the commit (`Apply`, `:151-160`, called from `CouncilRelay.cs:176-189`). The post-turn scan keeps hash and size only (`Core/Db/Database.cs:223-234`).
- So a refused plan is lost, and a refused JOURNAL loses that turn's new entries with it. A first-ever over-cap file has nothing to restore and stays on disk, unrecorded (`:130-137`).
- The agent is told `trading/archive/` is where old journal entries go (`WorkspaceBuilder.cs:571`); the notice tells it to move what no longer fits there (`:131-133`) — after the fact.
- Pinned today: `tests/TradeAgent.UnitTests/WorkspaceRevisionTests.cs` `An_over_cap_plan_is_refused_and_the_last_valid_revision_is_written_back` (`:153`),
  `A_journal_over_its_budget_is_refused_on_the_same_terms_as_the_plan` (`:192`), `An_over_cap_plan_with_no_earlier_revision_is_refused_and_nothing_is_written_back` (`:213`),
  `The_next_situation_says_what_was_refused_and_put_back` (`:234`); `TurnCommitTests.cs` drives the relay's commit.
Read first: `CLAUDE.md`; `docs/PRINCIPLES.md` (the memory paragraph); `WorkspaceRevisions.cs` whole; `CouncilRelay.cs:150-200`; `WorkspaceBuilder.cs:75-100`, `:560-580`;
`MissionSituation`'s `Restored` rendering; the four tests above.
Must NOT: change either cap or count bytes; split, trim or edit the agent's file (the class's rule: "Rejection restores; it never edits"); record the refused content as a revision
or let anything read the archive into a Situation or a packet; restore over a file whose kept copy is not on disk; overwrite an earlier kept copy; touch `out/` publications or the
relay's caps; add a migration or a schema rung (CLAUDE.md, no backward compatibility).
Items, one commit each, one-sentence messages:
1. **Keep before restore.** When `Snapshot` refuses a file that has an earlier accepted revision, the refused bytes it read are first written to
   `trading/archive/<PLAN|JOURNAL>-refused-<attempt id, or the UTC instant to the millisecond>.md` — a new file, never one that exists — and only then is the `Restore` returned.
   A copy that cannot be written means NO restore: the file stays as the agent left it, unrecorded, and the notice says it could not be kept. Inside the transaction is acceptable
   (an extra archive file is not destruction; the restore still happens after the commit). The first-ever over-cap case is unchanged.
2. **Say where it is.** The notice (and the `Rejected` activity line) names the kept path: "… revision N — the last one the app accepted — has been put back; what you wrote is kept at
   `trading/archive/PLAN-refused-….md`. Move what still matters into the plan under 60 lines." The Situation renders it as today.
3. **Tell the agent before it happens.** AGENTS.md's "Where things belong" line for `trading/archive/` (`WorkspaceBuilder.cs:571`) adds that a plan or journal refused for size is
   kept there by the app, and how to recover it.
Red-first tests (each quoted red at base): (a) the existing plan test extended — the kept file exists with exactly the over-cap content, the notice names it, the plan on disk is the
last accepted revision; (b) `A_refused_journal_keeps_the_turns_new_entries` — the new entries are in the kept copy; (c) `No_restore_without_a_kept_copy` — the kept path made
unwritable (a FILE named `trading/archive`) ⇒ no `Restore`, the over-cap file unchanged on disk, the notice says not kept; (d) `A_second_refusal_never_overwrites_the_first_copy`;
(e) plan and journal refused in one turn ⇒ two kept files. Mutant to watch red and quote: `Snapshot` returning the `Restore` without writing the copy ⇒ (a) and (c) red.
Gate and report per `docs/HOW-WE-BUILD.md` and `docs/FLEET.md` "The builder pass": rebase on `main` first; `--no-incremental` Release 0 warnings; three suites 0 failed;
`WorkspaceRevisionTests` and `TurnCommitTests` 3×; branch CI on all three platforms; tests box or NOT RUN with `ready`'s answer; names vs `main` 0 removed (both set sizes printed);
`## Report` ≤ 20 lines appended here. No push to `main`, no merge.
