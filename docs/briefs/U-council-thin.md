# U-council-thin — two roles, run serially by the app, handing each other work through a relay that cannot lose or double it
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/COUNCIL.md` (all of it), `docs/RESUME-HERE.md` step 6. Branch `u-council-thin`,
worktree `~/Projects/ai-trading-software-for-mihael-worktrees/U-council-thin`, rebased onto `main` first (it carries `U-wakes`:
`mission_event`, `MissionEventStore`, the wake consumed inside `AiAttemptStore.Begin`). Every guard ships with a test that was
RED before it and ONE mutant watched going red, quoted. No box, no ATAS, no money. Schema: the next free number at your rebase.

**Why.** Everything assumes one agent: one `AgentHome` (`Paths.cs:39`), one `AGENTS.md` (`WorkspaceBuilder.cs:40-54`), one cached
conversation (`AppHost.cs:61-86`), one model, one cap (`Trading.cs:140,155,245,529`), no `role` on `ai_attempt` or
`mission_event`, one card. The doctrine: roles that survive agents, the app relaying a report up and an agenda down.

1. **Roles as data and folders.** Two roles: `operations` (the chair; the existing `workspace/agent` becomes its home so
   `PLAN.md`/`JOURNAL.md` survive) and `research` (new, `workspace/research`). `WorkspaceContext.Role`; `Build(role)` writes each
   role's mission (shared rules, then a role section — Operations: the owner's words, allocation inside the cap, the agenda;
   Research: hypotheses, data, backtests, the report; both told what they may read and write: their own folder and `in/`,
   never the other's — a convention under codex, said so); `role` columns on `ai_attempt` and `mission_event` (additive);
   `role_model` and `role_share` per role in settings (defaults `gpt-5.6-sol`, 50/50 of `AiDailyCostCap`), two Safety rows.
   RED: `Build("research")` yields no mission. Mutant: both roles given one mission.
2. **One scheduler, serial.** One loop; the next due event across roles (owner messages → operations; inbox → both; review
   tick and `self` per role; fills and orders → operations; renewal → both); one conversation per role, one process at a
   time, `AgentPresence.Shared` for both; admission per role AND global (`AdmitsAnotherTurn(role)`); the card names the
   active role. RED: two due events, one per role → the first turn ends before the second starts; mutant (share dropped).
3. **The relay, committed in one transaction.** A Research turn ends with `out/report-<attempt>.md` (≤ 20 lines, the app
   REJECTS a longer one, keeps the last valid); the app publishes it: a `publication` row (id = sha256 of the content, role,
   attempt, revision, recipients, classification, created_at), a `delivery` row per recipient, and ONE Operations event
   `task:<publication-id>` (kind `report`, uniquely keyed) — one `Database.Write`; then the copy into
   `roles/operations/in/<publication-id>.md`; a delivery is `delivered` only after the copy. The reverse path publishes
   Operations' `out/agenda-<attempt>.md` (≤ 40 lines) as `brief` events to Research. On start and after every turn the app
   reconciles disk against the tables: an unpublished report is published (idempotent by hash), a delivery with no file
   re-copied. RED (the property in `docs/COUNCIL.md`): a throw injected at EACH boundary (after the file, after the
   transaction, after the copy), then a fresh host over the same database → exactly ONE `task:` event and one file, never
   zero, never two; mutant (the event id from the attempt, not the hash) → two.
4. **The Situation per role:** Operations sees the owner's words FIRST, then its deliveries (the report's text and id);
   Research sees its briefs; both see their role, share remaining and wake reasons. `MissionInstructionsTests` pins both
   role sections; `docs/CONTRACTS.md` gains the relay's tables; `USER-GUIDE.md` one section; `Theme.cs` values only.

Not this unit: the daily report and the dispositions `delegated`/`blocked`/`superseded` (`U-report`); grants enforced in tool handlers (`U-api-worker`); leases (`U-council-concurrent`).

Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file
→ 0 failed; touched classes 3×; a `Timing` red re-run alone 3×, never loosened. Commit per item, one sentence, no trailers.
Append `## Report` (≤20 lines): tip sha, gate counts, one line per item with its RED and mutant, what you did NOT do.
