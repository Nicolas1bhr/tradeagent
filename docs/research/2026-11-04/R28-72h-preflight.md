# R28 — Pre-flight review of the 72-hour run (2026-11-04)

**What it is.** A pre-flight review of main `92204b30` (code as at `2a4a7edc`; the tip adds only a docs note) against the 72-hour run of R25 § 2:
MVP-L, M-org1 sitting #2. The question is what would stop, stall, corrupt or quietly degrade that run before hour 72.

**What it covers.** The run's shape is from R25 § 2 and § 4/4b; the roadmap is R26. On the owner's Linux server, from a fresh home: onboarding on paper; a
chief appointed and activated; Start the AI. Then there is no owner input for 72 hours, inside the founding 40-list-dollar day-total. During the run:
- one charter is made;
- hires activate themselves (D3);
- executors author and backtest strategies and ask for verdicts;
- eligible versions go to paper under the founding envelopes: 5 pairs, 5,000 USDT and 3 deployments each (D11);
- fills, P&L and costs reach their authors;
- the organisation keeps, kills or iterates;
- one service restart happens;
- the owner talks to his chief.

**What it is not.** Findings only: no product code was changed. A cloud reviewer made it in one sitting. Nothing in it touched a venue, a model API,
money or credentials.

**Labels.**
- PROVED: a scratch test ran against main's code; it is quoted, and it is not committed.
- RUN: the main test suite ran here.
- READ: the code was read at the `file:line` given, and nothing ran.
- INFERENCE: this file's reasoning.

Every READ finding was traced by a reviewer and re-read at its key line before being written here. Hours are estimates (INFERENCE) unless marked otherwise.
**Severity:** STOPS = the MVP's arrow cannot complete; DEGRADES = it completes worse, later or more expensively; COSMETIC = wrong words or wasted work only.

**Toolchain.** .NET SDK 10.0.114 (Ubuntu package). `dotnet build TradeAgent.sln` succeeded with 0 warnings and 0 errors.

## 0. Verdict

1. **The run as specified cannot close its evidence loop on main.**
   - D12 (36 months, last 12 held back) is not on main: the holdout is 3 months of 12 (§ 1, E1).
   - Every trial therefore comes out of the 50-trial exploration pot, which nothing renews (E2).
   - Renewal needs ≥ 30 fresh days of monthly archive (E3), so it cannot happen inside 72 hours.
   - Expect research to be refused in words from somewhere between hour 6 and hour 36. From then on, turns are paid to be told so.
2. **"Kill" on paper does not kill.** Stopping a deployment (D9, or the owner's own stop) is undone on the next sweep. PROVED (P1).
   - A paper slot, once allocated, is never freed, so a pair's third version is its last for the run.
3. **The founding risk policy contradicts the founding envelopes.**
   - `MaxOrderQuantity = 1` refuses normal-sized SOL, BNB and XRP entries.
   - `MaxOpenPositions = 2` caps the whole paper account at two instruments (P2).
4. **The loop has three single points of stall that an unattended run will meet:**
   - one tools-unavailable stream marks every luna seat dead until a restart (W1);
   - a vendor turn has no wall-clock bound (W2);
   - an empty team envelope puts the whole loop to sleep until the owner's midnight (W3).
5. **A restart loses work it should keep.** A quit-cancelled turn settles its wakes `Failed`, not `LOST`. The owner's retried words go to the sleeping
   legacy chair, not the chief (R1, R2).
6. **What holds** (checked, not re-reported):
   - wakes are consumed in the same commit as the launch row;
   - open attempts become `LOST` at their reservation on start;
   - orphan cgroups are killed;
   - door and pipe inputs are size-capped and refused cleanly;
   - ids for deployments, fills and verdicts are idempotent;
   - the day window is the owner's local day, the same in every reader.

   Details are in § 6.

## 1. Ranked findings

| # | Finding | Where | Bites (h) | Severity | Basis |
|---|---|---|---|---|---|
| E1 | D12 not on main: the holdout is 3 of 12 months, so no verdict is ever conclusive and nothing can be promoted | `Core/Data/EvidenceDefaults.cs:25`, `Core/Data/BinanceArchive.cs:36`, `Referee.cs:801` | 0 | STOPS | READ |
| E2 | Every trial is exploration (nothing promotable), so 50 trials per pair, not 200; a full pot never arms renewal | `Core/Db/CampaignStore.cs:937–967`, `Core/Db/CampaignRenewal.cs:80`, `Core/Trading.cs:582–620` | 6–36 | STOPS | READ |
| P1 | A stopped paper run is restarted on the next sweep; the allocation stands and the slot is never freed | `Gateway/TradingGateway.cs:1386–1420` (`HoldsItsSlot` :1442), `:1882–1940` | first stop | STOPS (keep/kill) | **PROVED** |
| P2 | Founding `MaxOrderQuantity = 1` and `MaxOpenPositions = 2` against envelopes of 50 SOL, 10 BNB, 5,000 XRP | `Core/Board/BoardFounding.cs:46–66`, `Gateway/TradingGateway.cs:4246` | first entry | STOPS (3 pairs) | READ |
| W1 | `DeadSeats` marks (runtime, profile, model) dead for the life of the process on one matching stream | `AgentRuntime/DeadSeats.cs:14–30`, `RuntimeManifest.cs:1347` | any | STOPS | READ |
| W2 | No wall-clock bound on a vendor CLI turn; a hung turn holds the position's lease and reservation until a restart | `AgentRuntime/MissionLoop.cs:3395`, `AgentSession.cs:1248`, `Core/SeatSpec.cs:16–17` | any | STOPS (that position; all if chief) | READ |
| W3 | Only unaffordable roles due → the loop waits until local midnight, uncapped, missing others' future-due wakes | `AgentRuntime/MissionLoop.cs:2974–2984`, `:3340`, `:4800–4836` | 6–20 daily | DEGRADES (hours) | READ |
| R1 | A quit-cancelled turn settles every wake `Failed`; recovery keys only on `LOST` | `MissionLoop.cs:2554–2579`, `:4585–4598`; `Core/Db/OrgStore.Mandates.cs:1083–1115` | restart | DEGRADES | READ |
| R2 | `RetryOnce` re-raises the owner's words to `CouncilRoles.Operations` (asleep while a chief is active) | `MissionLoop.cs:4625–4634` vs `MissionEventStore.cs:793` | restart; any failed chief turn | DEGRADES | READ |
| W4 | `next.json` self-wake has no floor and a 30-min cap, for org positions too | `MissionLoop.cs:4770–4773`, `:4241–4258`, `:216` | 0 | DEGRADES | READ |
| W5 | A role refused at admission for its seat stays oldest-due and blocks the serial lane, retrying every 5 s | `MissionLoop.cs:3290–3340`, `:3986` | any | DEGRADES→STOPS | READ |
| W6 | No automatic luna → sol fallback; each refused turn is charged its reservation and settles its wakes `Failed` | `AgentRuntime/BrokerSeat.cs:47`, `MissionLoop.cs:4919` | any | STOPS if luna is refused | READ |
| E3 | Renewal needs ≥ 30 fresh days and a monthly archive download: impossible within 72 h | `Core/Db/CampaignRenewal.cs:65, 204, 263` | 6–36 on | STOPS (by design; see E2) | READ |
| E4 | After the first spent campaign, `TapeHoldout` withholds recent tape and forward bars of every pair from agents | `Core/Db/CampaignRenewal.cs:29` | day after E2 | DEGRADES | READ |
| B1 | Reservation is conditional on pricing: luna at list price is 0.16 a turn; owner rates (2/8) or sol on a team seat is 2.56 or 3.20, over a 0.25 × 0.25 team's 2.50, and refused for good. D13 is not on main | `Core/SeatSpec.cs:42`, `Core/Trading.cs:770`, `RuntimeManifest.cs:~2095`, `Core/Db/OrgStore.cs:656` | 0 | STOPS (conditional) | READ |
| G1 | Paper day notes re-published on every 5-s replay with current totals, so a new publication, delivery and `in/` file per past day per new fill | `Gateway/ForwardRuns.cs:303–305`, `Gateway/TradingGateway.cs:2310–2348` | ≤ 24 on | DEGRADES | READ |
| P3 | The note's "net" is Σsells − Σbuys − fees: cash flow, not P&L, while a position is open | `Gateway/TradingGateway.cs:2316–2320` | first day-close note | DEGRADES (the author's keep/kill input) | READ |
| W7 | Strict class priority: housekeeping (the 24-h ceiling) can starve behind self-waking research-class wakes on one lane | `Core/Db/MissionEventStore.cs:886–899`, `AgentRuntime/Lanes.cs:93` | 12 on | DEGRADES | READ |
| G2 | Material scan: one `synchronous=FULL` write per tracked file every 30 s, inline on the background loop; past 5,000 files the scan window freezes and removals go unrecorded | `Core/Db/MaterialStore.cs:68–101`, `Core/MaterialScanner.cs:143–308`, `App/AppHost.cs:2172` | 30–60 | DEGRADES | READ |
| G3 | Every 5-s refresh rebuilds every page, with per-position scorecards over unindexed `org_forecast.forecaster` and `org_mandate_event.kind` | `App/MainWindow.cs:629–642`, `Core/Db/OrgStore.Scorecards.cs:244` | 24–48 | DEGRADES | READ |
| R3 | A repeated `charter` door call is a new act (fresh request id) and spends `StructuralPerWeek = 2` | `AgentRuntime/BrokerDoor.cs:241–244`, `Core/Db/OrgStore.Verbs.cs:25, 392–404` | 0–12 | DEGRADES | READ |
| P4 | One inconclusive slot per pair (⌊3/3⌋), held 90 days, never freed (P1) | `Core/Db/AllocationStore.cs:676`, `Inconclusive.cs:47` | first verdicts | DEGRADES | READ |
| W8 | A due wake that cannot be served (deferred tape, refused host) makes `Idle` return 5 s: a full sweep with writes every 5 s | `MissionLoop.cs:4190`, `:3926–3935` | any | COSMETIC–DEGRADES | READ |
| C1 | Wording: an envelope refusal tells the owner "the day's AI spending ceiling is reached"; `OwnCannotFund` promises a reset that never fixes an unfundable team; the allocation note says "Nothing runs it yet" | `MissionLoop.cs:2979`, `Core/Trading.cs:923`, `Gateway/ForwardRuns.cs:1063` | — | COSMETIC | READ |
| C2 | Vendor holds and backoffs in memory only; mixed `+00:00`/`Z` timestamps in the cell-proof tables; codex rollouts in broker seat homes never pruned; replay-from-minute-one cost | `MissionLoop.cs:3540–3550`, `CellAssuranceStore.cs:77`, `RuntimeManifest.cs:1101`, `ForwardRuns.cs:206` | — | COSMETIC | READ |

## 2. Evidence — the loop's dead end (E1–E4, P4)

**E1. D12 is on the record, not in the code.**
- What main has: `EvidenceDefaults.HoldoutMonths = 3` and `BinanceArchive.MonthsWanted = 12`.
- What that means for a verdict:
  - The α-test runs only over ≥ 365 known days (CONTRACTS; R27), so every verdict is inconclusive: `loss-over-a-short-holdout` or `too-few-events`.
  - A version created after the holdout began can never be Promoted (`ScoringPolicyV1`, `Referee.cs:801`: PrecedesTheFreeze), and on history that is always the case.
  - Paper is reached only as PaperEligible, net positive over 3 months, or through the one inconclusive slot (P4).
- Fix: land D12, i.e. HoldoutMonths 12 and MonthsWanted 36.
- R27 already measured the consequence for the 12-and-3 split.

**E2. The exploration pot is the real budget.**
- The founding campaign holds 200 trials, 3 verdicts and 50 exploration trials per pair (`Trading.cs:582/598/620`, copied per pair at
  `TradingGateway.cs:2495`).
- `IsExploration` is true unless a version's parent is Promoted. Nothing is Promoted (E1), so all trials come out of the 50-trial pot, and `Refusal`
  refuses after that.
- `SpentOn` counts a campaign spent only at TrialsCharged ≥ 200 or at the verdicts. A full exploration pot never arms a forward holdout.
- So the organisation runs out in two ways:
  - **verdicts:** 15 in all (3 × 5), likely gone by hours 6–24;
  - **trials:** 250 in all, likely gone by hours 12–36 on one lane.
- After that, every backtest or verdict is refused in words. Turns keep spending the plan on refusals, and no throttle was found (READ).
- Fix: count the campaign spent when its exploration pot is full and no parent is promotable, or treat PaperEligible as a promotable parent while no
  promotion is possible.

**E3. Renewal needs data the run cannot get.**
- It needs ≥ 30 fresh days after a cutoff at the next UTC midnight after arming (`CampaignRenewal.cs:65, 204`).
- It also needs a fresh archive download (`:263`), and Binance publishes monthly files after each month ends.
- Forward bars collected live do not count.
- So D7's renewal is about 1–2 months away whatever the run does. That is by design, but it means the 72-hour run's evidence arrow is single-shot per pair.
- The organisation's only remaining lever is paper, and P1 and P4 bound that.

**E4. Recent data goes dark on every pair.** From the midnight after the first campaign is spent, `TapeHoldout` withholds forward bars and tape of
every pair from every pipe caller. The paper runner is exempt.
- Fix: hold back only the spent campaign's own pair.

**P4. One inconclusive slot per pair, for 90 days.** It also charges one trial. Expect at most 5 inconclusive versions on paper, all placed early.

## 3. Paper (P1–P3, G1)

**P1 — PROVED.** A scratch `[Fact]` was added to `tests/TradeAgent.FaultTests/PaperDeploymentTests.cs`, reusing its `Ready`/`Allocated` rig. It ran
once and was then removed; it is not committed. The test:
- starts the allocation's run;
- calls `EndPaperDeploymentAsync(run.Id, "stopped by org/team-x")` (the D9 verb's path, `GatewayPipeServer.cs:4125`);
- advances one minute and sweeps.

Output:
```
Passed TradeAgent.Tests.Fault.PaperDeploymentTests.R28_scratch_a_stopped_run_is_restarted_on_the_next_sweep [526 ms]
 R28 deployment c462d0bd2e64 alloc=f1ddb6fa0a47 active — -
 R28 deployment 5cab7c0b699c alloc=f1ddb6fa0a47 ended — stopped by org/team-x
 R28 sweep after the stop started: 1
```

Why it happens:
- `HoldsItsSlot` frees an ended run once it is flat.
- `StartPaperDeploymentsDueCore` then starts a replacement for the still-standing allocation. V1 paper rows have `EffectiveTo = null`, and the founding
  envelopes have `Days: null`.
- The existing `A_standing_paper_allocation_starts_one_deployment…` asserts this replacement on purpose (an owner's stop as "replace once flat"). D9's
  stop, and an organisation's kill, need the other meaning.
- Occupancy is counted from allocations, so the pair's 3 slots fill and never empty.

Fix: an org or owner stop writes a superseding allocation row with `EffectiveTo = now`, and the sweep skips allocations whose last run ended "stopped by …".

**P2. The risk limits bind below the envelope.**
- `MaxOrderQuantity = 1` is enforced on the runner's order path (`TradingGateway.cs:4246`).
- A run's capital is 1,666.67 USDT (5,000/3; seen as `MaxNotional = 1666.66666666` in the RUN below), sized as fraction × capital / price.
- At the 2026 levels the founding floors are half of (SOL 200, BNB 1,000, XRP 2 USDT), a full-size entry is SOL ≈ 8, BNB ≈ 1.7 and XRP ≈ 830, so each is refused. BTC and ETH stay under 1.
- `MaxOpenPositions = 2` is account-wide across 5 pairs and up to 15 runs. `MaxOrdersPerMinute = 6` is account-wide too.
- Fix: a founding `MaxOrderQuantity` per pair at least the pair's envelope quantity divided by 3, and `MaxOpenPositions ≥ 5`.
- (READ and arithmetic. The refusal sentence itself was not run.)

**P3 and G1. The note an author reads.** `TellResearchAboutARun` is called at every UTC-day boundary in the replay. Every pass replays from minute one,
every 5 s.
- The content carries the run's current totals, and the publication id hashes the content. Once a fill lands, every past boundary yields a new
  publication, new deliveries and a new `.md` in research's and the author's `in/`.
- The wake is deduplicated by event id, so no turns are bought. The files feed G2.
- Its "net" is cash flow, so a long position reads as a loss of about its notional.
- Fix:
  - say it only on a live bar (`live` is already computed below);
  - build the figures through that day, so the id is stable;
  - mark open quantity at the last close;
  - add an index on `fill(account_id, connector, at)`.

**Handled:**
- the forward-bar collector polls each deployed pair every minute, backs off to 5 min and catches up;
- the runner steps every 5 s;
- `max_decision_age` refuses stale orders;
- restart replays deterministically, with idempotent request ids;
- the 5,000 USDT per pair is enforced in `RecordPaper` and per order.

## 4. Wakes, budget and model calls (W1–W8, B1)

**W1. DeadSeats.** One stream matching "Defaulting to fallback metadata" or "Code Mode is unavailable because" marks (runtime, profile, model) dead in a
static dictionary.
- Every luna seat, the chief's included, is then refused for the life of the process.
- The message itself says so: "will not start this AI seat again until its program, its settings or its model change, or TradeAgent restarts".
- An unattended run has no restart.
- Two reviewers found it independently.
- Fix: a TTL (about 1 h), or a re-run of the surface dry-run before keeping the mark.

**W2. No turn bound.** `SendMissionAsync(prompt, own.Token)` is cancelled only by a fence or a pause. `AgentSession` reads stdout to EOF. The cgroup
bounds processes and memory, not time.
- A codex stall (a network hang, a cell command that never exits) holds `_turning` and the LAUNCHED reservation until a restart.
- Fix: a wall-clock cap per seat class (20–30 min), linked into `own`, so the fence teardown runs and the wakes are released.

**W3 and W4. A team runs dry and the org sleeps.**
- How a team runs dry: `AskedForDelay` clamps `next.json` to ≤ 30 min with no floor. `org-wake`, which is floored and bounded
  (`OrgStore.Autonomy.cs:33–80`), is refused to cell seats (`GatewayPipeServer.Structure.cs:250`). So an executor can re-wake every few seconds to
  30 min.
- The arithmetic: at the measured ≈ 0.11 per luna turn (D8), a 0.25 × 0.25 team's 2.50 holds about 22 turns a day. Executors self-waking every 30 min
  spend it by mid-day (INFERENCE).
- What happens then: if only that team's roles are due, `Pick` returns `CappedUntilMidnight(...)`. For an envelope refusal that is
  `ResumesAt − now`, uncapped by `MaxDelay` or the next due time.
- A future-dated `Self` or ceiling wake of the chief or a head never nudges the loop. Only a fresh raise does.
- Two reviewers found it independently.
- Fix:
  - use the midnight wait only when `!spend.AdmitsGlobally`, else `Idle(events)`;
  - clamp org positions' `next.json` through `RewakeFloors.Of(policy)`, with one pending `Self` wake.

**W5. A role refused for its seat blocks everyone.**
- A `HostOrLaunch` refusal (seat or profile refused, signed out, DeadSeats) keeps the wakes and returns `BusyRetry` (5 s).
- With founding `Lanes = 1`, the refused role stays oldest-due and is picked first on every tick.
- W1 feeds this path.
- Fix: a per-role rest (as `LedgerRefused` has), skipped in serial `Pick`.

**W6. luna refused.** `BrokerSeat.Fallback` is a catalog choice only.
- A "model not served" refusal that misses the usage-limit pattern does three things:
  - it exits with no usage and is charged its reservation;
  - it settles its wakes `Failed`;
  - it backs off 30 s, doubling to 30 min.
- That is about 48 failures a day per role, which empties a team in about 15.
- Fix: a refusal pattern in `runtimes.json` mapped to "before any work" (charged zero, wakes given back), and a code switch to the fallback.

**W7. Ceiling starvation.**
- How it happens: `Queue` orders by class first, and the 24-h ceiling is the lowest class. With one lane and self-waking research-class positions, a head
  whose only wake is its ceiling can wait indefinitely.
- Fix: age an overdue wake into the research class.

**W8.** Unservable due wakes poll at 5 s with a full sweep and its writes. There is no spend, but there are constant fsyncs. Fix: `Idle` ignores the rows
`Pick` excluded.

**B1. Reservation (D13 not on main).**
- How the reservation is priced: `SeatSpec.Reserved` is at least 1.2 M in and 20 k out (`TurnAllowance.Default`), priced at max(uncached,
  cache-write) + output.
- The owner's Safety-page rates win over list price.
- Luna at list price is 0.15 + 0.01 = 0.16. Sol is 3.20, and the owner's 2/8 rates give 2.56 (U-loop-e2e's figure).
- Above 2.50, `Unfundable` refuses a team charter and the gate refuses every team-funded launch, permanently.
- **Before pressing Start, read `AiPriceInputPerMillion` and `AiPriceOutputPerMillion` on the run's home.** Either land D13, or leave the rates unset
  and the team seats on luna.

**Handled:**
- each row's day is its `started_at`'s day, so a turn crossing midnight counts once;
- one window serves every envelope;
- usage-limit refusals before any work are charged zero;
- refused admissions consume nothing;
- an idle legacy division counts 0 at charter.

## 5. Restart (R1–R3)

**R1. A clean stop loses the wakes in flight.**
- systemd's SIGTERM leads to `PauseAsync`, which cancels each turn. The row closes ENDED, failed, and `Settle` marks every wake `Failed`; only the
  owner's words are retried.
- The recovery for LOST turns does not run, because the row is ENDED, not LOST. That recovery is `RecoverLostTold` and the mandate re-raise of
  deliveries, escalations and proposals.
- What is lost: delivery and escalation judgments, `Activated`, fill and output wakes.
- After that, mandate holders re-wake on their floors, heads only at their 24-h ceiling, and members never.
- An executor hired just before the restart, with no mandate, may never turn.
- Fix: when the loop's own pause cancelled the turn (no fence), `Release` the wakes as the unworked branch does, or close the row as LOST.
- The same holds for a turn that fails for any reason (wakes reviewer: `Settle` gives back nothing but the owner's words). Extend `LostWake` to
  `Activated` and `Self`.

**R2. The owner's retried words go to the wrong role.**
- `RetryOnce` raises with `CouncilRoles.Operations` ("the chair's, as the message it retries was").
- Since D4, the original went to `OwnerRecipient()`, which is the active chief.
- After a failed, lost or quit-cancelled chief turn, the owner's message waits on a role the gate keeps asleep. The owner's line "talk to his chief"
  breaks at the restart.
- Fix: `e.Role ?? events.OwnerRecipient()`.

**R3. Charters are not idempotent.** Each door call gets `door-` + a new GUID. A model unsure whether its charter went in, and repeating it, spends the
72 hours' second structural act (`StructuralPerWeek = 2`).
- Fix: refuse a charter identical in caller, kind and rationale hash within N minutes.

**Handled:**
- open attempts become LOST at their reservation on start;
- leases are in memory;
- `cgroup.kill` sweeps leftover `attempt-*` cgroups (`KillMode=mixed`, `Delegate=yes`);
- a wake is consumed with its row before the launch;
- deployments, ops, fills and verdicts are idempotent;
- WAL with `busy_timeout = 5000`, and a single-instance lock.

**NOT VERIFIED:** whether the app survives a restart of `tradeagent-display.service` (Xvnc, `Restart=always`). `tradeagent.service` `Requires=` it, and
an X server that goes away under Avalonia may crash the app (and be restarted) or hang it.

## 6. Growth and time (G1–G3, time)

**Rates assumed.** 20–40 turns an hour and about 10 door calls per turn give, by hour 72, about 1.5–3 k `ai_attempt` rows, 15–30 k `tool_call` rows and
a similar number of `mission_event` rows. That is small for SQLite. What matters is what is re-read or re-written per pass: G1 (§ 3), G2 and G3 (§ 1).

**Handled:**
- `activity`, `engineering_log` and `health_event` are rotated;
- plan and journal files are capped;
- the position packet has a byte budget;
- tape wakes are coalesced;
- cell codex homes are removed per attempt;
- resumed sessions rotate after 20 turns;
- the Activity page reads `LIMIT 200`.

**Time.**
- The AI day is the owner's local day (`OwnerDay`, `TZ=@ZONE@` in `packaging/linux/tradeagent.service`), with the offset taken at each boundary. No DST
  change falls in a run starting after 2026-10-25 in Europe.
- `mission_event` times are one fixed-width UTC format.
- Waits use `Task.Delay` re-derived from wall time and capped at 30 min, except on the W3 path.
- Paper notes and the loss day turn at UTC midnight while the AI day turns at the owner's. Two "days" on one page is cosmetic, and documented for the loss
  budget (`TradingGateway.cs:3173`).

## 7. Main is red — what the RUN shows

`dotnet test tests/TradeAgent.UnitTests --filter "OrganisationLoopTests|OversightPageTests"` here: 1 failed, 7 passed. The failure is the end-to-end
loop at `OrganisationLoopTests.cs:292`:
```
Assert.Single() Failure: The collection contained 2 items
```
A temporary print, reverted, showed two standing paper allocations on BTCUSDT, each with an active deployment in the same envelope:
- the winner, under `allocation-policy-v1`;
- a second version, under `allocation-policy-inconclusive-v1`.

Both had `MaxNotional = 1666.66666666`. That reads as D11's amended inconclusive path now placing a version the test predates, not as a duplicated
allocation (INFERENCE). The separate fixing run owns it. The Oversight failure named in RESUME-HERE did not reproduce in this filter on Linux; it was not
pursued.

## 8. Before pressing Start (smallest set)

1. Land D12 (E1), and either count a full exploration pot as spent or let PaperEligible parent refinement (E2). Without these the evidence arrow ends
   early.
2. Make a stop end the allocation (P1). Set the founding risk policy to fit the envelopes (P2).
3. Add a TTL to DeadSeats (W1), a wall-clock bound per turn (W2), and the non-global envelope wait (W3) plus a floored `next.json` (W4).
4. Route `RetryOnce` to the owner's recipient (R2), and release wakes on a quit-cancelled turn (R1).
5. Check the home's AI price settings, or land D13 (B1).

The rest can wait for what the run measures.

## 9. NOT VERIFIED

- Every READ row: none was run.
- The hour estimates: none was measured.
- Whether codex in practice emits W1's strings.
- The fsync cost of G2 and G3 on the server's disk.
- SQLite's actual query plans.
- The Xvnc-restart behaviour.
- Any real model turn.
- Anything on Windows.

*Records findings only; it marks nothing fixed.*
