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
Tip `896ea28`, on `main` `f40f833`. Gate, all in Release on the rebased tip: `dotnet build TradeAgent.sln -c Release
--no-incremental` → `0 Warning(s) / 0 Error(s)`; unit `Failed: 0, Passed: 529`; integration `Failed: 0, Passed: 615,
Skipped: 1`; fault `Failed: 0, Passed: 277`; the 21 touched test classes 3× → `Failed: 0, Passed: 186` each time, no
re-runs needed. Test names vs `main`: 19 added, **0 removed**. Schema is **8**: `U-data-binance` had not landed at my
final rebase (`main` still reads `DatabaseSchemaVersion = 7`), so 8 is mine — whoever lands second renumbers.
1. **Roles as data and folders** — verified, not taken on trust. RED (`Build` reverted to one home): `Assert.Equal()
   Failure: Strings differ | Expected: ···"…/research" | Actual: ···"…/agent"`. Mutant (`RoleSection` switching on
   `CouncilRoles.Operations`): `Assert.Contains() Failure: Sub-string not found | Not found: "## Your role: the Research
   Director"`. Its two Safety rows were **missing**; I built them in item 4.
2. **One scheduler, serial** — RED (loop reverted to `events.Due` + `_host.Conversation`): `the chair's turn consumed the
   Research Director's wake`. Mutant (`AdmitsAnotherTurn => AdmitsGlobally`, the share dropped): `Assert.False() Failure
   | Expected: False | Actual: True`. Peak concurrent turns measured, not assumed: 1.
3. **The relay, one `Database.Write`** — RED (`CouncilRelay.Run` a no-op, the pre-unit state): all three boundaries red,
   `Assert.Throws() Failure: No exception was thrown | Expected: typeof(System.IO.IOException)`. Mutant (task id from
   `p.Attempt`, not the hash): `Assert.Single() Failure: The collection contained 2 items`, 5 of 7 relay tests red.
4. **The Situation per role** — RED (deliveries block removed): `the chair was not told what the report said`. Mutant
   (deliveries rendered above the owner's words): `another agent's report was put above the owner's words`.
Uncommitted files from the killed builder: **all six kept**; only `IMissionHost.Relay()` changed, to `Relay(string role,
string? attempt)`, so a publication records which attempt produced it. Nothing discarded.
NOT DONE: no Windows box, no ATAS, no order, no real CLI run — every claim above is this Mac's suite. No grant table
beyond `publication.recipients`/`classification` and the `role` columns; grants are still enforced by nothing
(`U-api-worker`). No leases (`U-council-concurrent`), no daily report or dispositions (`U-report`). The Situation has no
snapshot id or freshness stamp. The relay's source-event consumption is still `AiAttemptStore.Begin`'s, from `U-wakes`.
