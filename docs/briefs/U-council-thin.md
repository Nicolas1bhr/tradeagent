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

## Report
Built at `896ea28` on `main` `f40f833`; rebased over `main` `9b4de6f` (`U-data-binance`, schema 8) to **`e2eda82`**.
Gate on the rebased tip, Release: `dotnet build TradeAgent.sln -c Release --no-incremental` → `0 Warning(s), 0 Error(s)`;
unit `Failed: 0, Passed: 558`; fault `Failed: 0, Passed: 277`; integration `Failed: 0, Passed: 621, Skipped: 1`; the 21
council + 5 dataset unit classes 3× → `Failed: 0, Passed: 194` each and `DataOverPipeTests` 3× → `Passed: 6` each, no
re-runs needed. Test names, one extractor over both revs: 1179 → 1196, 17 added, **0 removed**.
**Schema is 9**, not 8: `U-data-binance` took 8 first, so the council's `if (have < 9)`, its `Versioning.cs` paragraph, its
pin (`CouncilRoleTests.The_role_columns_arrive_at_schema_nine…`) and item 1's subject say 9; the dataset stays at 8.
Conflicts, resolved inside the rebase, both sides kept by name: `Database.cs` (dataset at 8, then the council at 9);
`Versioning.cs` (both paragraphs); `Paths.cs` (`Data` and `.Concat(CouncilRoles.All.Select(RoleHome))` in one `foreach`);
`MissionEventTests.cs` (both empty-table assertions, one merged comment). `CONTRACTS.md`, `WorkspaceBuilder.cs`,
`AppHost.cs`, `MissionLoop.cs`, `Trading.cs`, `MissionInstructionsTests.cs` auto-merged; every `data-*`, `MarketData`
and `Paths.Data` line of `main`'s checked present. Deliberately replaced: `DatasetLedgerTests`' `Assert.Equal(8, …)` →
the floor form `>= 8` its own comment calls the convention (its NAME is kept, so no test name is removed), and the
pointer comments in `AiAttemptLedgerTests` and `MissionEventTests`, which now name the council's test.
Items unchanged — 1 roles (RED `Actual: ···"…/agent"`, mutant `Not found: "## Your role: the Research Director"`);
2 scheduler (RED `the chair's turn consumed the Research Director's wake`, mutant `Expected: False | Actual: True`);
3 relay (RED three boundaries, `No exception was thrown`, mutant `contained 2 items`); 4 Situation (RED `the chair was
not told what the report said`, mutant `above the owner's words`). NOT DONE: no box/ATAS/order/CLI run; no grants
enforced (`U-api-worker`), no leases, no daily report; the rebase adds no behaviour.
