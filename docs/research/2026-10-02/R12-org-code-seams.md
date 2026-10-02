# 12 — The organisation plane on today's code: seams, mapping, decision rights, allowance, smallest migration (read-only)

> **Erratum, 2026-10-02 (manager, after R17 #23 and R18 § 2):** the rung numbers below predate the plan's final ladder — `U-cost-model` keeps 27;
> `U-org-ledger` (O1) and `U-venue-verify` each take the next free rung at landing (expected 28 and 29), and M0 no longer waits for any slice after O1.
> O2 was split into `U-org-principals` and `U-org-rights`; per-seat allowances left O4 for the CARD `U-org-seats` (R18 § 4). `docs/ORGANISATION.md` § 15 governs.

HEAD `1275aff` (`main`, clean), 2026-10-02. Nothing under `src/`, `tests/` or other docs was changed; nothing was built, tested or launched.
**Labels:** every `path:line` is **SOURCE@1275aff** (abbreviated as R01: `Core/` = `src/TradeAgent.Core/`, likewise `AgentRuntime/`, `Gateway/`, `App/`,
`Security/`, `Provisioning/`). **RUN** = executed by this leg: `grep`, `git log`, and `scratchpad/r12/allowance.py` (arithmetic over the HIST and SOURCE
figures it names). **HIST(date)** = `BUILD-STATUS.md`. **PLAN §** = the owner's `TRADEAGENT_AUTONOMOUS_FACTORY_PLAN_2026-09-27.md` (outside the repo,
written at `309f389`). **INFERENCE**, **UNKNOWN** as named. The requirement is the owner's 2026-10-02 direction quoted in this leg's brief; it amends
`docs/PRINCIPLES.md`:39-45 and PLAN §§ 10.4, 11 (no fixed hierarchy). **Words used:** *unit* (organisation, division, team: one table, any depth);
*position* (a durable seat in a unit — "top-level manager" = head of a division, "head manager" = head of a team, "executing agent" = member); *head* (the
position a unit names as its decider); *seat* (runtime + model + per-turn allowance); *attempt* (one paid launch filling a position = today's `ai_attempt`).

## 0. Ten-line answer

1. **The substrate exists.** "Role" is an unconstrained TEXT principal on eight tables (`Core/Db/Database.cs`:521-546, 630, 658, 707, 956; RUN: no CHECK constraint in the schema) and on every launch grant (`Security/AgentGrants.cs`:147): a position can be a row whose id IS that string — legacy ids `operations`/`research` kept, no history rewritten.
2. **What blocks a third position is code, not tables:** 121 `CouncilRoles.` references in 29 files (RUN), with three collapses — unknown → the chair (`Core/Council.cs`:53, the key of open attempts and loop counters), unknown → the chair's folder `agent` (`Council.cs`:80, so its CLI would resume the chair's codex session, `AgentRuntime/RuntimeManifest.cs`:572-575), and any non-Research output → an agenda for Research (`AgentRuntime/CouncilRelay.cs`:587-608).
3. **The budget does not scale:** an equal 1/N share (`Core/Trading.cs`:311-314) against luna's 0.324 USD reservation under the 5 USD cap means N ≥ 16 positions can never launch and N = 15 get one turn a day; at attempt 2's mean turn cost a 30-minute review per position costs 7.02 USD/day at N = 10 (RUN).
4. **Turns are serial:** the product's only caller of `TurnAsync` is one `LoopAsync` (`AgentRuntime/MissionLoop.cs`:1332, 1391; RUN grep), so N positions queue behind one turn; the per-role lease exists, but the single `_minted` id slot (`AgentRuntime/TurnMeter.cs`:515, 777, 866) is safe only serially.
5. **Attempt 2's binding allowance was the vendor's plan, not the cap:** 48 turns and 1.9397 of 5 USD, then "try again at 10:30 PM" (HIST 2026-10-01); 0.0146 USD per turn measured, 0.0404 blended with four reservations kept as cost, ≈ 57 turns and 2.30 USD per running hour (RUN).
6. **Enforceable decision rights have five homes:** `OrgStore` as sole writer, the relay pass (who may address whom), the backtest/verdict handlers, the admission transaction (`Core/Db/AiAttemptStore.cs`:256-281) and the app-written wake queue — hard for harness seats, advisory for CLI seats until containment (`state/` is agent-writable, `docs/CONTRACTS.md`:229-231).
7. **Money does not move into the org:** `MayPlaceOrders` stays `role == "operations"` (`Council.cs`:63; `Gateway/GatewayTypes.cs`:270), never derived from headship; no org verb is a pipe write; operator controls stay in-process.
8. **Smallest migration (§ 5):** O1 ledger (rung 29) → O2 positions as principals → O3 wakes ∥ O4 envelope tree → O5 assignments, reports, escalation (rung 30) → O6 head verbs + `org` read → O7 owner page → O8 standing, O9 parallel turns; the legacy pair seeded as heads of two divisions.
9. **Collisions are few and nameable:** the ladder (RUN: `Core/Versioning.cs`:274 `DatabaseSchemaVersion = 26`; 27 `U-cost-model` and 28 `U-venue-verify` first), `TurnMeter`/`AiAttemptStore`/`CouncilRelay`/`DailyReports` with `U-decision-port`, `Protocol`/the drain table with `U-tape-read`; O1 fits W4b beside `U-timeframe-b`; O3's core (`MissionLoop`, `MissionEventStore`) is in no queued brief.
10. **UNKNOWN (§ 7):** who holds restructure rights (the owner's "agents shouldn't choose anything" against an org "restructuring itself"), the owner's AI budget and runtime, and the containment route that would make org rules hard for CLI seats.

## 1. Seam table — where the two fixed roles are baked in

| # | Where | Controls | Today | Org replacement |
|---|---|---|---|---|
| 1 | `Council.cs`:41 `All`, walked at `MaterialScanner.cs`:100, `Paths.cs`:107, `WorkspaceBuilder.cs`:104-105, `CouncilRelay.cs`:250, `BoundaryStore.cs`:423/602/622/818, `MissionLoop.cs`:1299/1861, `App/AppHost.cs`:858, `DailyReports.cs`:752, `TurnMeter.cs`:556/852, `Trading.cs`:314 | the universe every enumerator walks | two constants | `OrgStore.ActivePositions()`; `All` kept as the legacy pair (635 test references in 50 files, RUN) |
| 2 | `Council.cs`:44, 53 `Default`/`Or`, used at `TurnMeter.cs`:1124, `MissionLoop.cs`:1245-1263/2277, `MissionEventStore.cs`:192, `CouncilRelay.cs`:500, `AiAttemptStore.cs`:299, `DailyReports.cs`:881 | attribution keys | NULL **and unknown** → chair | NULL → chair (history only); live keys = the exact position id; unknown → refused or quarantined |
| 3 | `Council.cs`:46 `IsKnown` at `Backtests.cs`:211 (`RoleOf`, also `GatewayPipeServer.cs`:2454), `Holdout.cs`:73 | who may backtest / ask a verdict | two names | "an active position" (OrgStore); app principals `referee`, `allocator` (`Referee.cs`:74; `TradingGateway.cs`:274-278) and the queued `perception` never become positions |
| 4 | `Council.cs`:63; `GatewayTypes.cs`:270; `GatewayPipeServer.cs`:1178-1190 | order permission | `role == operations` | **unchanged** — bound to the legacy id, never to headship |
| 5 | `Council.cs`:66-71 `Title`; `ApiConversation.cs`:433, 439; `MissionLoop.cs`:681, 929 | names in prompts and refusals | switch | `org_position.title`, a claim, rendered quoted |
| 6 | `Council.cs`:80 `HomeDir` (unknown → `agent`); `Paths.cs`:50, 107; `WorkspaceBuilder.cs`:50, 79, 91; `AppHost.cs`:263-264; `MaterialScanner.cs`:99-102; `CouncilRelay.cs`:570 | home folder, scan coverage, CLI session continuity | a third role runs in the chair's folder; `resume --last` is cwd-filtered (`RuntimeManifest.cs`:572-575) | `org_position.home_dir`; new positions under `workspace/org/<id>/`; the scanner walks active positions |
| 7 | `MissionLoop.cs`:1853-1882 `Schedule` (review 30 min, :129; renewal) | scheduled wakes | per role, every role | per position by policy: heads review; members wake on assignments and deliveries only |
| 8 | `MissionLoop.cs`:1477-1517; `MissionEventStore.cs`:476-488 `RolesDue`, :452-464 `DueFor`, :513-520 | who turns next (oldest due, free, affordable, not held) | already any string (`COALESCE(role,$chair)`) | same algorithm over positions; an ended position's wakes are settled, never launched |
| 9 | `MissionLoop.cs`:1066, 1182-1192 lease; :1332, 1391 one loop; :1222-1232 material pass | concurrency | per-role lease; serial turns; a pass needs every agent quiet | per-position lease kept; K concurrent turns only in O9 |
| 10 | `MissionEventStore.cs`:388-396 (owner words → Operations); `MarketDataService.cs`:194-206 (data → Research); `Referee.cs`:417, `TradingGateway.cs`:426, 1163 (verdict and paper notes → Research) | wake routing | fixed recipients | legacy routes kept; notes also reach the requesting position when it is not legacy |
| 11 | `AiAttemptStore.cs`:256-281 `Begin`, :425-445 `Totals` by role; `Database.cs`:521 | attempt attribution, per-role totals | role TEXT | role = position id (no schema); subtree totals in O4 |
| 12 | `Trading.cs`:297, 311-314 `RoleShare`/1-over-N; `TurnMeter.cs`:824-853 `RuleFor`/`Share`; `Trading.cs`:871-872 | the second admission gate | 1/N = 50/50 | a unit envelope tree; legacy `RoleShare` read as the two divisions' shares |
| 13 | `Trading.cs`:689-691 `TurnAllowance`; `TurnMeter.cs`:881-897 `Reservation` | per-turn reservation | one global 1.2 M in / 20 k out | per-seat allowance (enforced by the harness, advisory on a CLI) |
| 14 | `TurnMeter.cs`:504-506, 638, 653, 718, 804-806 open/staged slots; :515, 777, 866 `_minted` | which open attempt a turn closes | `Or`-keyed; one minted slot | exact id; mint-and-reserve per turn (O9) |
| 15 | `Trading.cs`:259, 276, 284, 303 `RoleModel`/`RoleRuntime` ("the roles are data"); `AppHost.cs`:194-198, 205-208, 229-249, 161-167, 636-650 | seat: runtime, model, tools, metering | chair = vendor CLI always; Research = harness when a key is held | per-position seat inside the owner's allowed classes; members default to the harness |
| 16 | `MissionLoop.cs`:1143, 1767-1810 | vendor usage-limit hold | keyed by runtime | unchanged — the seat's runtime is the key |
| 17 | `CouncilRelay.cs`:250-254 `Reconcile`, :500 `Fence`, :587-588 `KindFor`, :599-600 `KindsFor`, :607-608 `RecipientsOf`; `MissionLoop.cs`:720-721 | what a role publishes, to whom | Research → report → chair; else agenda → Research | report → own unit head; assignment → a position in own subtree; escalation → parent head |
| 18 | `PublicationStore.cs`:219-260 `Commit` (publication + delivery + paid wake, one transaction) | the work-order primitive | recipients from #17 | unchanged; recipients from OrgStore |
| 19 | `BoundaryStore.cs`:422-435 (a wake for every role), :602 `COUNT(DISTINCT role) >= All.Length`, :622, :818 | consequential boundaries | every role assesses | **kept** for the legacy pair: a two-senior ceremony this migration does not generalise |
| 20 | `Backtests.cs`:162-165 (one run per role), :209-216, :257-264 (program read from the role's folder); `GatewayPipeServer.cs`:2454, 2487 | evidence spending per caller | per role | per position; verdicts narrowed to heads, member runs scoped to an assignment (O6) |
| 21 | `GrantedWorkerTools.cs`:40-41, 74, 111-120, 142; `AgentGrants.cs`:147-160; `AgentSession.cs`:211, 515; `GatewayPipeServer.cs`:837, 1166 | identity carriers | a role string | the position id; shape unchanged |
| 22 | `WorkspaceBuilder.cs`:16, 602-664 `RoleSection` (unknown → ""), :197 ("write and run code" told to every role) | instructions | two fixed texts | generated: unit charter (claim) + rights and members (app facts) |
| 23 | `DashboardView.cs`:434, 1556-1560, 2037-2138 (Research share slider); `DailyReports.cs`:228, 752, 881-911; `DailyReport.cs`:558, 709 | owner surface | two rows | units, positions, envelopes; a line per unit |
| 24 | `AgentSupervisor.cs`:80; `CliAgentRuntime.cs`:652-665; `AppHost.cs`:67-90 (the chat metered as Operations, :83) | the Chat page is the chair's | — | unchanged: the owner's liaison stays the legacy chair |

**PLAN's seams re-checked:** every seam PLAN § 3.2 names exists at `1275aff` at the lines above; the six harness tools (`GrantedWorkerTools.cs`:49-54) and
`Verified = false` (`RuntimeManifest.cs`:490, 642, 684, 694) are unchanged. RUN `git log 309f389..HEAD`: 44 commits, seven touch these files — vendor holds keyed
by runtime, `StartTheAiAsync`, the inbox-origin wake, the kernel peer check off Windows — and none moves a role seam. **Correction to R01 § D.8** ("search
breadth is two turns at a time"): mission turns are serial (#9); the only other launch path is the owner's typed turn on the chair's conversation,
admitted through the same ledger (`TurnMeter.cs`:982).

## 2. Mapping — organisation concept → existing primitive → owner (M = app measurement, C = agent claim)

| Concept | Extends | Owned by | M / C |
|---|---|---|---|
| Unit and its parent (root = the mission, head NULL = the owner in-process; divisions; teams; depth bounded) | none — new; the `parent_version_id` lineage pattern (`Database.cs`:1212) | `org_unit` (OrgStore, sole writer) | M: id, parent, head, status, envelope; C: title, charter (a publication) |
| Position (head or member, by the unit's `head` column — no kind enum) | a role and its folder, "roles survive agents" (`Council.cs`:3-20; `docs/COUNCIL.md`:56-58) | `org_position` | M: app-minted id (legacy ids kept), unit, home, status; C: title |
| Seat (runtime + model + per-turn allowance + price basis) | `RoleRuntime`/`RoleModel` (`Trading.cs`:259, 276), `RuntimeForRole` (`AppHost.cs`:194-198), runtime rows, `ListPrices.cs`:82-84, `TurnAllowance` (`Trading.cs`:689) | nullable columns on `org_position`; the owner's allowed classes in settings | M (policy); a head's model preference is C until applied |
| Agent = one attempt filling a position | `ai_attempt` + per-launch grant (`AiAttemptStore.cs`:256; `AgentGrants.cs`:147) | unchanged | M |
| Native helper inside a turn | PLAN § 10.1 (no rows) | charged to the attempt | M (cost only) |
| Assignment / work order with acceptance criteria | brief + delivery + one paid wake in one transaction (`PublicationStore.cs`:219-260); fixed-grammar lines parsed from agent prose, as `BoundaryDeclaration` does (`BoundaryStore.cs`:83-110) | `org_assignment` (rung 30) referencing its publication | M: issuer, holder, state, due, expiry, app-checkable criteria (e.g. a COMPLETED run); C: criteria text and results |
| Decision record | precommitted RECOMMENDATION/BASELINE scored at review (`BoundaryStore.cs`:83-110, 310, 815-829) | `org_event` for each act; publication for the rationale; boundaries unchanged | M: the act, its disposition, the score; C: the rationale |
| Report up | publication kind `report` (`CouncilRelay.cs`:587) | publication + delivery to the own unit's head | C content; M delivery |
| Escalation | boundary deadline + code default (`MissionLoop.cs`:1474, 2051); the owner's activity log and daily report | new kind `escalation` → parent head; at the root → owner surface, never a permission request | C content; M routing, deadline, default |
| Performance record | `ai_attempt` (cost), `tool_call` (refusals), `strategy_version`/`strategy_run` (role, attempt), promotion → allocation → fill lineage, `DirectorRecord` | computed at read — no table | M only; self-assessment is C |
| Restructure: create, merge, retire a unit | none — new; trial and verdict charges carry no role (`Database.cs`:787-795, 822-828), so nothing resets | `org_unit` status/parent + `org_event` | request C; application M |
| Hire / retire a worker (= a position) | `WorkspaceBuilder.Build` (home); grants per launch; LOST keeps the reservation (`AiAttemptStore.cs`:387) | `org_position` + `org_event` | M |
| Budget envelope per unit | `RoleShare`, `AiAdmissionRule` (`Trading.cs`:297; `AiAttemptStore.cs`:119-160) | share columns on `org_unit`/`org_position` | M; a requested child envelope is C until applied |
| Wake / eligibility; owner liaison | `mission_event.role` (`MissionEventStore.cs`:182-192); owner words → chair (:388-396) | unchanged | M |

## 3. Decision rights — what code can enforce, and where

"Hard" = enforced by an app code path the seat cannot bypass. A **harness seat** has no shell and its identity is fixed in-process (`GrantedWorkerTools.cs`:274;
`AppHost.cs`:161-167). A **CLI seat** runs unconfined as the owner's user: it can write `state/` (`docs/CONTRACTS.md`:229-231) and the pipe's peer check
has a same-user gap (:219-223), so every right below is advisory against it until containment (`R-containment`, `U-contain-2`). "Hard" binds a harness
seat's own actions only: while any CLI seat runs on the machine it can rewrite the org ledger itself, so the ledger's integrity is advisory until then.
Rows naming `OrgStore` or a new argument are proposed checks at an existing code point (INFERENCE); the other points are SOURCE.

| Right | Enforcement point | Harness | CLI |
|---|---|---|---|
| Unit, position and assignment ids are minted by the app; no op writes org rows | `OrgStore` sole writer; the closed op table (`Core/Protocol.cs`:14-142) and the dispatch default (`GatewayPipeServer.cs`:1244) — a test enumerates ops | hard | advisory |
| A caller is the position its launch was granted | grant per launch (`AgentSession.cs`:515 → `GatewayPipeServer.cs`:837, 1059-1077); in-process role for the harness | hard | peer check, same-user gap |
| Only a head assigns work, and only into its own subtree | the relay pass validating `TO:` against OrgStore (`CouncilRelay.cs`, the `Publish`/`Land` path) | hard | advisory |
| Only the parent unit's head creates, merges or retires a unit, or hires or retires a position, inside depth, count and envelope bounds; divisions and the legacy pair only by the owner | `OrgStore.Apply` inside one `Database.Write` on the relay pass | hard | advisory |
| Only heads spend the scarce verdict budget for their unit (a narrowing) | verdict handler (`GatewayPipeServer.cs`:2454) | hard | advisory |
| Members spend trials only under an assignment they hold | backtest handler (`Backtests.cs`:92) checking an `assignment` argument | hard | advisory |
| Members get paid turns only for assignments and deliveries | `MissionLoop.Schedule` (:1853); wakes app-written only (`PublicationStore.cs`:219-260; no op raises one) | hard | advisory |
| A unit spends within its envelope, the envelope within its parent's, the root within the owner's cap | `AiAttemptStore.Begin` (:256-281), one transaction | hard | advisory (the cap is in `kv`, `TradingGateway.cs`:2160, 2247) |
| The app launches one turn per position at a time | `MissionLoop.Lease` (:1182) | hard | hard for app launches; a CLI can start vendor runs from its shell |
| No discretionary order from any new position, head included | `MayPlaceOrders` (`Council.cs`:63; `GatewayPipeServer.cs`:1178) | hard | pipe-enforced, same-user gap |
| Mode, kill switch, live activation, approvals, updates, credentials | absent from every org verb and op (CLAUDE.md); owner controls in-process | hard | the existing `U-contain-2` gap, unchanged |
| A restructure resets no evidence budget | trial and verdict keys without role (`Database.cs`:787-795, 822-828) | hard | advisory |

**Advisory only, for every seat (prompt text):** the quality of a head's decisions; consulting members first; following an assignment's text; "doing the
work smartly"; not reading a peer's folder ("a convention, not a wall", `WorkspaceBuilder.cs`:629, 659); honest reports; when to escalate; what a title or
charter means. **What code must enumerate (INFERENCE):** the verbs it checks (assign, report, escalate, create-unit, merge, retire, hire, submit-for-verdict)
and the bounds that keep the scheduler, the scanner and the reservation tree finite (depth, positions per unit, total positions) — because an enforcement
point is a closed, default-deny switch. Titles, unit names and the number of units stay data; head versus member is a column on the unit, not a role enum.

## 4. The AI allowance — the org's binding constraint

**Metered today:** a reservation committed before launch, compared and inserted in one transaction with the day's and the role's totals, consuming the
wakes in the same commit (`AiAttemptStore.cs`:256-281); a daily cap of 5 USD whose raise asks twice (`Trading.cs`:460-468); the role share as a second gate
(`Trading.cs`:871-872); a global 1.2 M / 20 k allowance (`Trading.cs`:689-691) priced at the role's model at the cache-write rate (`TurnMeter.cs`:881-897);
a turn that reports no usage keeps its reservation (`AiAttemptStore.cs`:312-349) and a restart turns open rows LOST at their reservation (:387); an
unpriceable runtime "reserves nothing, and then the ceiling holds nothing back" (`TurnMeter.cs`:878-879); the harness bounds a turn at 24 requests, 512 KB of
tool output and the token allowance checked between requests (`ApiConversation.cs`:64-78, 379-393); a vendor's usage limit is seen only when it refuses, and
holds that runtime (`MissionLoop.cs`:1767-1810).

**Attempt 2, the only real-model run (HIST 2026-10-01):** "one turn can cost up to 6.4 USD" with gpt-5.6-sol, so luna was chosen, "0.324 USD reserved a turn"
(`BUILD-STATUS.md`:6792-6794); turn 1 "167,264 input (141,568 cached), 4,336 output, 0.0132 USD" (:6805); "Five turns by 15:54Z, 0.037 USD in all" (:6807);
end state "48 turns — Operations 43 (1.5065 USD), Research 5 (0.4332 USD) — 1.9397 USD of the 5 USD cap, four of them charged their 0.324 reservation"
(:7008-7009); the run ended on the owner's ChatGPT Codex plan, "You've hit your usage limit … try again at 10:30 PM" (:7006), USD figures being
"list-price equivalent; the codex CLI is billed to the owner's ChatGPT plan" (:6802-6803). Running windows 15:48:32–15:58Z and 17:27–18:08Z (:6803, :6936,
:6915, :7006) ≈ 50.5 min (INFERENCE from those stamps).

| Derived (RUN `allowance.py`) | Value |
|---|---|
| Measured-only cost (1.9397 − 4 × 0.324) over 44 turns | 0.6437 USD → **0.0146 USD/turn**; blended 0.0404 USD/turn; reservation ÷ measured mean = **22×** |
| Per running hour | ≈ 57 turns; **2.30 USD charged**, 0.76 USD measured (the mean is dominated by blocked and churn turns: a floor, not a research-turn price) |
| Launches in flight at the 5 USD cap with nothing spent | 15 at luna's 0.324; **0 at sol's 6.4** |
| Turns per position per day under today's 1/N share (full allowance → a 200 k / 8 k executor seat, reserving 0.0596) | N=2: 149 → 167 · N=5: 47 → 65 · N=10: 13 → 31 · N=15: **1** → 19 · N=16: **0** → 18 · N=20: **0** → 14 |
| A 30-minute review for every position (48 turns/day each, `MissionLoop.cs`:129) | N=5: 3.51 USD/day · N=10: **7.02 USD/day > the cap**; the cap pays heartbeats for 7.12 heads and nothing else |

**What an N-position org needs from the metering (INFERENCE from the above):** (1) **a reservation tree** — at `Begin`, for every ancestor of the launching
position, spent + reserved of its subtree + this reservation ≤ its cap, read inside the same `Database.Write` as the INSERT, so sibling teams cannot both
take a parent's last room; positions never change unit, and a re-parented unit carries its spend with it while the root sum is invariant; (2) **a per-seat
allowance and price** — today the price is per role but the allowance is global, so a cheap executor reserves like a director; (3) **unknown ≠ zero kept
and tightened** — LOST/unreported unchanged; an app-created seat whose runtime cannot be priced is not seated; (4) **the vendor plan as a shared, unmetered
allowance** — every CLI seat on one plan draws on the limit attempt 2 hit after 48 turns, the hold per runtime is already right, and harness seats on an API
key spread load onto a second, per-token allowance; (5) **no heartbeat for members** and heads' review interval sized from their envelope; (6) concurrency
bounded by in-flight reservations, not by a separate knob. A CLI seat can still start vendor runs from its shell that the app never meters (`Containment.cs`:43-45;
PLAN § 12.3 case 3) — one more reason for members on the harness (`docs/COUNCIL.md`:120, "Workers run on an app-owned harness; seniors may keep the vendor CLI").

## 5. Smallest additive migration — ordered slices

Each slice is one builder leg, behaviour-preserving before it adds anything, and leaves the legacy pair's every test name and outcome unchanged.
Next free rung: **29** (RUN: `DatabaseSchemaVersion = 26`; 27 and 28 are claimed by `U-cost-model` and `U-venue-verify`; `state/tape.db` is its own ladder).
A separate org database file is not an option: the main DB is WAL (`Database.cs`:31) and the envelope check must share `Begin`'s transaction (INFERENCE from
SQLite's documented per-file atomicity of attached databases in WAL mode, not re-checked here).

| Slice | Closes — observable result | Files | Rung | Depends |
|---|---|---|---|---|
| **O1 `U-org-ledger`** | the org is app-minted data: on upgrade the DB holds root `org` (head NULL = owner), `div-operations` (head `operations`), `div-research` (head `research`), the two positions with homes `agent`/`research`; `OrgStore` sole writer; nothing reads it yet; no op writes it | `Core/Db/Database.cs`, `Core/Versioning.cs`, new `Core/Db/OrgStore.cs`, CONTRACTS | 29: `org_unit`, `org_position`, `org_event`; nullable seat and share columns | `U-venue-verify` landed |
| **O2 `U-org-principals`** | a third position is a row, not a constant: a test-seeded member gets its own home under `workspace/org/<id>/`, conversation, attempts, grant and exact fence; may backtest; is refused orders (mutants: `Or` back in `TurnMeter.Key`; `HomeDir` fallback; `MayPlaceOrders` by headship) | `Council.cs`, `Paths.cs`, `MaterialScanner.cs`, `WorkspaceBuilder.cs`, `AppHost.cs`, `TurnMeter.cs` (Key), `MissionLoop.cs` (counters), `MissionEventStore.cs` (`For`), `CouncilRelay.cs` (Fence), `Backtests.cs`, `GatewayPipeServer.cs` (verdict `RoleOf`), `ApiConversation.cs` | — | O1 |
| **O3 `U-org-wakes`** | heads keep review and renewal; members wake only on deliveries; fair over N; an ended position's wakes settled with a disposition; notes also reach a non-legacy requester | `MissionLoop.cs` (Schedule, selection, card), `MissionEventStore.cs`, `MarketDataService.cs`, `Referee.cs`:417, `TradingGateway.cs`:426/1163 | — | O2 |
| **O4 `U-org-envelopes`** (money-adjacent: red-first, mutants) | the reservation tree in `Begin`; legacy 50/50 read as division shares; per-seat allowance; an unpriceable app-created seat refused; refusals name the unit (mutants: subtree check removed ⇒ sibling race red; totals read outside the transaction ⇒ red) | `AiAttemptStore.cs`, `TurnMeter.cs` (RuleFor, Share, Reservation), `Trading.cs`, `AppHost.cs`:636-650, `MissionLoop.cs`:917-935 | — | O2, `U-decision-port` |
| **O5 `U-org-assignments`** | `out/assignment-<attempt>.md` (`TO:`, `ACCEPT:`, `DUE:`) validated against the subtree, published, recorded, delivered with one paid wake; a member's report names its assignment and goes to its head; escalation to the parent head, at the root to the owner's activity and daily report; an overdue assignment expires by code; a bad address quarantined with the reason in the writer's next Situation | `CouncilRelay.cs`, `PublicationStore.cs`, `OrgStore.cs`, `MissionLoop.cs` (Situation), `WorkspaceBuilder.cs` (RoleSection) | 30: `org_assignment` | O3, `U-decision-port` |
| **O6 `U-org-verbs`** | `out/org-<attempt>.md` requests — create-unit, merge, retire, hire, child envelope — applied by the app within bounds and recorded with their disposition; legacy pair and divisions refused to agents; new positions are harness seats until containment's record; verdicts narrowed to heads and member backtests to an `assignment` they hold; read op `org` (my unit, head, members, assignments, envelope, spend) with a zero-path drain row, `trade org`, and a line in the harness tool list | `OrgStore.cs`, `CouncilRelay.cs`, `Backtests.cs`:92, `Protocol.cs`, `GatewayPipeServer.cs`:300-353, dispatch, verdict :2454, `GatewaySchema.cs`, `TradeCli/Program.cs`, `GrantedWorkerTools.cs`:111-120, `WorkspaceBuilder.cs` | — | O4, O5, `U-tape-read` |
| **O7 `U-org-surface`** | an Org page: units, heads, members, seats, open assignments, envelope and today's spend per unit; raising an envelope is a grant (two presses), lowering or pausing a unit's work one press, distinct from Pause AI and the kill switch; one report line per unit; the Research share slider becomes the division row | `DashboardView.cs` (or a new view), `DailyReports.cs`, `DailyReport.cs`, `AppHost.cs`:858 | — | O4 |
| **O8 `U-org-standing`** | per position and unit, computed at read: cost, assignments opened/closed/expired and cycle time, versions/runs/verdicts authored, refusals, calibration of scored decisions; no envelope moves on it before a family's 90 days (`docs/EDGE-FACTORY.md` § 5) | a reader beside `OrgStore`, `BoundaryStore.cs` (records), the `org` op | — | O5–O7 |
| **O9 `U-org-parallel`** | up to K concurrent turns (owner setting, default 1 = today); mint-and-reserve per turn; the material pass keeps a bounded quiet window | `MissionLoop.cs` (Loop, Turn, Pass), `TurnMeter.cs` (Mint, Begin), `AppHost.cs` | — | O4; after an observed org run |

**Conflict matrix with the eleven queued briefs** (R = ladder order; H = same methods: land in order, the second re-checks; M = same region; L = same file,
another region; — = no shared file. A "—" or "L" pair may run in parallel, L with a rebase; M, H and R pairs are ordered. INFERENCE from each brief's named
edits; re-check at dispatch):

| Queued (wave) | O1 | O2 | O3 | O4 | O5 | O6 | O7 | O8 | O9 | Shared |
|---|---|---|---|---|---|---|---|---|---|---|
| `U-key-host-pin` (W1) | — | L | — | L | — | — | L | — | L | `ApiConversation`, `TurnMeter.Charge`, `DashboardView` key box |
| `U-cost-model` (W1) | R | L | L | L | — | L | L | — | — | rung 27, `Backtests`, `Referee`, `Trading.cs`, `SettingsView` |
| `U-runner-forward` (W2) | — | — | L | — | — | — | — | — | — | `TradingGateway` (order gates vs note recipients) |
| `U-evidence-identity` (W2) | — | L | — | — | — | L | — | — | — | `GatewayPipeServer` verdict handler (:2484 vs :2454) |
| `U-timeframe-a` (W3) | — | L | — | — | L | — | — | — | — | `WorkspaceBuilder` (language section vs RoleSection) |
| `U-tape-store` (W3) | — | L | — | — | — | — | L | — | — | `AppHost` (:476-560 vs :161-264), `SettingsView` |
| `U-venue-verify` (W4) | R | L | — | — | — | L | L | — | — | rung 28, `Backtests`, `Protocol` texts, `AppHost.StartAsync` |
| `U-paper-friction` (W4) | — | L | — | L | — | L | M | — | — | `WorkspaceBuilder`, `Backtests`, `Trading.cs`, `GatewaySchema`, `DailyReport.cs` |
| `U-timeframe-b` (W4b) | — | — | — | — | — | — | — | — | — | `ForwardRuns` only |
| `U-tape-read` (W5) | — | L | — | — | L | M | M | L | — | `Protocol`, drain table, dispatch, `GatewaySchema`, `TradeCli`, `DailyReport.cs`, status |
| `U-decision-port` (W5) | — | H | L | H | M | — | M | — | M | `TurnMeter`, `AiAttemptStore` (Begin, Refuse, counters), `CouncilRelay.Fence`, `DailyReports`, `Trading.cs` |

**Waves:** W4b `U-timeframe-b` ∥ **O1** (no shared file; rung 28 landed first) · **M0** unchanged — nothing reads the ledger · W5 unchanged · W6 **O2** ∥ a tape
CARD unit · W7 **O4** ∥ **O3** (`MissionLoop` at different methods; land O4 first, it is the spend path) · W8 **O5** ∥ **O7** · W9 **O6** · an observed org run
on the owner's allowance (two divisions, one team, assignments) · then O8, O9. **Fast path** if the owner ranks the org above perception: O2 takes
`U-decision-port`'s W5 slot beside `U-tape-read` (shared: `WorkspaceBuilder`, L), and `U-decision-port` re-checks Fence and `TurnMeter` against O2 at dispatch.
**PLAN units this replaces:** O1+O2 = `U-worker-identity`; O3 = `U-worker-events` + `U-worker-scheduler`; O4 = `U-resource-tree`; O5/O6 =
`U-worker-capabilities` as graph predicates in handlers, and `U-durable-delegation` as the head-only `hire` (no model-wide `delegate_work`); O7 =
`U-delegation-observability`. PLAN § 25.4's precondition (containment before durable delegation) is met, by INFERENCE, if every hire is a harness seat:
a hire then adds no uncontained process, and O4's tree bounds what it can spend.

## 6. Protections touched, and how each slice keeps them

| Protection | Slices | How it is kept |
|---|---|---|
| Operator authority in-process only (mode, kill switch, live activation, approvals, updates) | O6, O7 | no org verb or op names any of them; O6's test enumerates ops and verbs; O7's controls are in-process; a head idling its unit is work, not the owner's pause |
| Money path rules 1–4 | none | no slice edits the order path, a connector or `TradingGateway`'s dispatch |
| `MayPlaceOrders` stays narrow | O2 | bound to the id `operations`; minted ids never equal it; agents cannot retire the legacy pair; O2's mutant on headship |
| Reserve before launch; unknown ≠ zero | O4, O9 | the tree is read inside `Begin`'s one transaction; LOST/unreported unchanged; unpriceable app-created seats refused; O9 mints and reserves per turn |
| Measurement versus claim | O1, O5, O6, O8 | `org_*` rows written only by `OrgStore`; titles, charters, criteria, rationales and reports are publications; standing computed at read; nothing an agent writes touches an org row |
| Inbox and prose as data | O5, O6 | the relay parses a fixed header grammar and validates it against OrgStore (the `BoundaryDeclaration` precedent); no text grants a capability, raises an envelope or reaches an operator control (PLAN Law 11) |
| Identity minted by the app | O1, O2, O6 | random ids from OrgStore; the grant carries the position id; a requested id or title grants nothing |
| No terminal | O1–O7 | seeding in the migration; the owner sees and bounds the org in the app; no file to edit, no shell instruction |
| Two presses for money and permission | O7 | raising an envelope is a grant (two presses, the `AiDailyCostCap` precedent); lowering or pausing is one |
| Evidence budgets and the holdout | O6 | restructures cannot reset trials or verdicts (keys without role); every position reads bars through `BarAudience.Pipe` (`Backtests.cs`:173) |
| Honesty about containment | all | CONTRACTS states that org rules are hard for harness seats and advisory against CLI seats until containment |

## 7. UNKNOWN — and what would close each

| UNKNOWN | Closes by |
|---|---|
| Who holds restructure rights: heads by request, deterministic app policy, or the owner ("agents shouldn't choose anything" against "the org restructuring itself") | an owner question before O6 |
| How many positions the allowance supports: the owner's daily AI budget and runtime are unanswered (`docs/EDGE-FACTORY.md` § 10 q6) | the owner's answer, then § 4's table re-run |
| The vendor plan's window, and whether concurrent CLI sessions share it | an observed run with two CLI seats; the vendor's own documentation |
| What a real research turn costs (attempt 2's mean is dominated by blocked and churn turns) | attempt 3's `ai_attempt` rows |
| Whether a multi-request CLI turn can exceed the 1.2 M input reservation | a read-only query of `max(input_tokens)` in attempt 2's and 3's databases |
| Harness overshoot: the allowance is checked between requests (`ApiConversation.cs`:379-393), so one request can pass it | a loopback test with a large final request |
| Whether the material pass starves under K > 1 concurrent turns (`MissionLoop.cs`:1222-1232) | O9's red-first test |
| The containment route, which decides when org rules become hard for CLI seats | `R-containment`'s decision record |
| Members on the harness cannot run code (`GrantedWorkerTools.cs`:49-54): whether "executing agents doing the work smartly" needs PLAN phase 4's execution environment | the observed org run |
| Whether boundaries grow beyond the two seniors, and who the owner's liaison is with several top-level managers | owner and manager decisions; kept legacy here |
| Whether all 635 test references survive O2 with names kept | O2's gate |
