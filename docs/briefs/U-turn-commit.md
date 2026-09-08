# U-turn-commit — a turn's output is staged, validated, committed as a revision bound to its attempt, and fenced
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/COUNCIL.md` (rules 5 and 6, "Memory in four tiers", the `U-turn-commit` line),
`docs/RESUME-HERE.md` step 6, then `CouncilRelay.cs`, `PublicationStore.cs`, `AiAttemptStore.cs`, `MissionLoop.TurnAsync`
(`:810-962`). Branch `u-turn-commit`, worktree `~/Projects/ai-trading-software-for-mihael-worktrees/U-turn-commit`, rebased
onto `main` first. Every guard ships with a test that was RED before it and ONE mutant watched going red, quoted. No box, no
ATAS, no money. Schema: 11 if `U-report` landed with 10, else 10 — read `Versions.DatabaseSchemaVersion` at your final rebase.

**Why.** The relay publishes `out/` files with `attempt` = whichever turn ran the relay (`CouncilRelay.cs:81-97`), so a KILLED
turn's report is published later under the NEXT turn's id or none (`MissionLoop.cs:929-943`): provenance names the wrong
launch. `trading/PLAN.md`, `JOURNAL.md` and memory are written freely — no cap, no revision (`WorkspaceBuilder.cs:236,238`);
`Valid` (`CouncilRelay.cs:200`) caps `out/` only. A turn's end is three writes (`End`, `Relay`, `Settle`). No fence exists: a
stale process can still drop a file the relay publishes. The scanner walks only `agent/` (`MaterialScanner.cs:50,65`).

1. **Staged output is bound to its attempt.** The Situation names the attempt id; the agent writes `out/report-<attempt>.md`
   (and agenda), `trading/PLAN.md`, `JOURNAL.md` as now. The relay attributes a file by the attempt id in its name: it must
   exist in `ai_attempt` (LAUNCHED or ENDED or LOST); a LOST attempt's file is published under that LOST id (the work is
   real, the provenance right); a file naming no known attempt is QUARANTINED to `out/quarantine/` with an activity line,
   never published — the fence. RED: a killed turn's report published under the next turn's id (today) → under its own,
   state LOST; mutant (attribution by "whoever ran the relay" restored) → red. Fence RED: an unknown-attempt file published.
2. **The plan and the journal become revisions.** At turn end the app snapshots `trading/PLAN.md` (≤ 60 non-empty lines) and
   `JOURNAL.md` (≤ 200; older entries the agent must move to `trading/archive/`) as `publication` rows of kind `plan`/`journal`,
   recipients = the role itself, classification `private`, content-hashed, revision-numbered; an over-cap or unreadable
   file is REJECTED and the last valid revision is WRITTEN BACK into the workspace, the activity line and the next
   Situation saying so (rule: an invalid publication is rejected and the last valid plan stands). RED: an 80-line `PLAN.md`
   survives the turn; mutant (the cap on `JOURNAL.md` dropped) → a 300-line journal accepted.
3. **One committed transition per turn.** `AiAttemptStore.End`, the attempt's publications (items 1–2), and the wake
   dispositions (`Settle`) commit in ONE `Database.Write`; a crash before it leaves LAUNCHED → `LoseOpen` marks LOST on the
   next start and the reconcile pass publishes that attempt's staged files under it. `NextRevision` moves inside the
   transaction. RED (the property in `docs/COUNCIL.md`): a throw injected before and after the commit, then a fresh host
   over the same database → exactly one revision per file and the attempt in one identifiable state, never a half; mutant
   (`End` outside the transaction) → an ENDED attempt with no publication.
4. **The scanner records every role.** `MaterialScanner` walks every role home's tracked dirs plus `in/` and `out/`
   (`Paths`, `CouncilRoles.All`), so what a role read and wrote is a `material` row; `runnable` unchanged. RED: a file
   in the Research home is unseen; mutant (`out/` dropped) → a published report unrecorded.
5. **One hash helper** (`Core/Sha256Hex`) replaces the four copies, output asserted byte-identical at each old call site;
   `CONTRACTS.md` gains the revision tables; the guide two sentences; the mission text names the attempt id and the caps.

Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file
→ 0 failed; touched classes 3×; a `Timing` red re-run alone 3×, never loosened. Commit per item, one sentence, no trailers.
Append `## Report` (≤20 lines): tip sha, gate counts, one line per item with its RED and mutant, what you did NOT do.
