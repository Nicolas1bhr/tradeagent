# Inspection of the reachable loop — sha `222dcc93c6527b81df9e56849f89997d967f1b76`, 2026-09-19

Labels: **SOURCE** = read in the tree at this sha (`file:line`); **RUN** = a command executed here, output quoted;
**HIST** = a claim taken from `BUILD-STATUS.md` / `docs/RESUME-HERE.md` with its date. Source inspection is not runtime
verification; a public method with no production caller is not a connected capability. Nothing was built or executed
against a provider, a venue or the Windows box.

## 1. Fits; preserve

| Primitive | Evidence | Property it protects |
|---|---|---|
| Default-deny tool surface | SOURCE `src/TradeAgent.AgentRuntime/GrantedWorkerTools.cs:49-54` (six names), `:132` (switch default refuses) | A name the model invents reaches no process, socket or path |
| Write fence | SOURCE `GrantedWorkerTools.cs:74` `Writable = [out/, trading/]` | A worker cannot rewrite its own mission file |
| One role gate, not two | SOURCE `GrantedWorkerTools.cs:16-22`; `src/TradeAgent.Gateway/GatewayPipeServer.cs:1154` | Tool route and pipe route refuse with the same line |
| Every call recorded, served or not | SOURCE `GrantedWorkerTools.cs:135`; `src/TradeAgent.App/AppHost.cs:158` (`ToolCallStore`) | Refusals are evidence, not silence |
| Measurement/claim split | SOURCE `src/TradeAgent.Core/Protocol.cs:35` (`material-list` vs `material-note`), `:41-47` (data ledger read-only) | An agent cannot edit what was observed about it |
| Backtest as an app measurement | SOURCE `Protocol.cs:93`; `GatewayPipeServer.cs:2264`; `src/TradeAgent.Gateway/Backtests.cs:422,439` | App parses, runs, hashes and records; no op edits a run |
| Holdout door | SOURCE `src/TradeAgent.Core/Data/BarFeed.cs:132`; `src/TradeAgent.Core/Data/Holdout.cs:79` (`BarAudience.Referee`) | Held-back bars have exactly one audience |
| Referee, charge first | SOURCE `src/TradeAgent.Core/Strategy/Referee.cs:177,188,273` | No verdict without a recorded, budgeted charge |
| Campaign trial/verdict budgets | SOURCE `src/TradeAgent.App/SettingsView.cs:476-483` (cutoff + campaign in one transaction) | Attempts against a holdout are counted |
| Promotion computed at read | SOURCE `src/TradeAgent.Gateway/TradingGateway.cs:148` (`Promotions.Standing`) | Eligibility cannot go stale in a cached row |
| Allocation ceiling on dispatch | SOURCE `TradingGateway.cs:1860-1878` (`ALLOCATION_NONE`) | A versioned intent with no standing capital is refused |
| Council boundaries and wakes | SOURCE `src/TradeAgent.Core/Db/BoundaryStore.cs:360`; opened `Referee.cs:379`; defaults applied `src/TradeAgent.AgentRuntime/MissionLoop.cs:1785`; surfaced `AppHost.cs:1186`, `src/TradeAgent.Gateway/DailyReports.cs:638,731` | A review has a deadline and a default no director can veto |
| Plan/journal revisions | SOURCE `src/TradeAgent.AgentRuntime/WorkspaceRevisions.cs:41,48,51,63` | Over-cap restores the last valid revision and tells the turn |
| Recovery/backoff and asked-for wakes | SOURCE `MissionLoop.cs:1948-1961`, `:2026` | A bad turn waits; a restart resumes the mission |

## 2. Concrete obstructions

| Action an agent needs | Restriction it hits | Smallest correction keeping the boundary |
|---|---|---|
| Ask for a verdict on its own candidate | No op and no verb: RUN `git grep -c 'Core.Ops\.' GatewayPipeServer.cs` → `60` references, none a verdict; SOURCE `TradingGateway.cs:112` exposes `Referee` in-process only | A read-only `verdict` op calling the existing `Referee.Verdict` under the caller's role/attempt, returning `RefereeFeedback.Text` (`Referee.cs:344`) — holdout bars stay behind `Holdout.cs:79`, the charge stays in `Referee.cs:188` |
| Have a campaign to be judged under | `Campaigns.OpenForDataset` reached only from the owner's holdout press — SOURCE `SettingsView.cs:476`, `:504` | Open within the already-granted research envelope on the existing dataset; never renew a budget or move a cutoff |
| Put an eligible version on paper | `Allocate` has one caller, RUN `git grep -c '\.Allocate(' -- src/` → `src/TradeAgent.App/DashboardView.cs:1`, the owner's press (SOURCE `DashboardView.cs:2248`) | An app-policy paper allocation for promoted versions, scoped so it cannot become live authority; leave `TradingGateway.cs:145` for live |
| Run code / build a tool | Six tools, none of them exec — SOURCE `GrantedWorkerTools.cs:49-54` | Blocker with nothing discarded: a bounded exec inside the role's home. Note the asymmetry in table 4 — the CLI role already has more |
| Delegate to a child | No spawn tool (SOURCE `GrantedWorkerTools.cs:49-54`); the role list is the app's (SOURCE `AppHost.cs:153`) | Blocker; not on the first paper slice's critical path |
| Collect market data | No op; owner press only — SOURCE `SettingsView.cs:164,338` | Reasonable: provenance is the point. Not a paper-loop blocker |
| Publish more than 20 report lines | SOURCE `src/TradeAgent.AgentRuntime/CouncilRelay.cs:57` (`ReportLines = 20`), `:64` agenda 40, `:71` 10 per pass | A size limit that rejects output without destroying it — quarantine keeps bytes (`CouncilRelay.cs:72`). Preserve |
| File an assessment with no baseline | SOURCE `BoundaryStore.cs:106-109` and `:229` (`BoundaryBaselines.Measurable/IsKnown`) | Format requirement with an accountability purpose: the baseline is compared at review. Preserve |

## 3. Missing connections

| Arrow on the loop | State | Evidence |
|---|---|---|
| Model turn → app | connected | SOURCE `AppHost.cs:113-141` (harness), `:198` — non-Operations roles on `ApiAgentRuntime` when a key is held; Operations stays on the vendor CLI |
| Turn → market data (read) | connected | SOURCE `Protocol.cs:47`; granted at `GrantedWorkerTools.cs:110` |
| Turn → program creation | connected, file-only | SOURCE `GrantedWorkerTools.cs:74` (`write_file` into `trading/`); the backtest reads the program out of the role's own home (SOURCE `Protocol.cs:80-93`) |
| Program → backtest | connected | SOURCE `GrantedWorkerTools.cs:101-107` (`Ops.Backtest` for every role); handler `GatewayPipeServer.cs:2264`; version accepted and run recorded `Backtests.cs:422,439` |
| Backtest → trial on a campaign | connected only when one is open | SOURCE `Backtests.cs:465` (`RegisterTrial`); opening is the owner press at `SettingsView.cs:476` |
| Candidate → submission for a verdict | **absent** | RUN `git grep -c 'referee.Verdict(' -- src/` → `0` production callers; four test files only (`tests/.../RefereeVerdictTests.cs`, `PromotionBoundsTests.cs`, `VerdictOverPipeTests.cs`, `DecisionFreshnessTests.cs`) |
| Verdict → agent | read-back connected, request absent | SOURCE `DailyReports.cs:602` (holdout run line) and `Protocol.cs:75` (`report` served verbatim); end-to-end exercised only at `tests/TradeAgent.IntegrationTests/VerdictOverPipeTests.cs:41` |
| Verdict → promotion row | in-process, unreachable | SOURCE `Referee.cs:273`; blocked by the row above |
| Promotion → paper allocation | owner press only | SOURCE `TradingGateway.cs:148` → `DashboardView.cs:2248`; no paper-specific path exists |
| Allocation → deployment | **absent** | RUN `ls src/TradeAgent.Core/Db | grep -ci deploy` → `0`; no deployment store, no runner |
| Deployment → forward intents | **absent** | RUN `git grep -l 'IntentDecision.From(' -- src/ | wc -l` → `0` (tests: 1); `StrategyEvaluator.Step` has one src caller, SOURCE `src/TradeAgent.Core/Strategy/Backtest.cs:460`; every `PlaceIntent` producer is manual or a close (`GatewayPipeServer.cs:2825`, `TradingGateway.cs:5442,5644,7954,7994`). The gateway says so: SOURCE `TradingGateway.cs:1853-1858` "no runner emits a live or paper intent yet" |
| Fills/costs → ledger and report | connected for orders that exist | SOURCE `Protocol.cs:24` (`pnl`), `DailyReports.cs:355`; nothing produces a paper fill from a strategy, and the built-in simulator's quotes are fixed (SOURCE `src/TradeAgent.Connectors.Fake/FakeBroker.cs:62-67`) |
| Fills → another autonomous decision | reads connected, decision unforced | SOURCE `GrantedWorkerTools.cs:101-110` (`pnl`, `report`); the next turn is scheduled from `.tradeagent/next.json`, capped at 30m (SOURCE `MissionLoop.cs:1948-1961`, `:78`) |
| Delegation (parent → child) | **absent** | SOURCE `GrantedWorkerTools.cs:49-54`; no spawn tool, no child identity minting |
| Contained code execution | **absent in the harness; unbounded in the CLI role** | SOURCE `GrantedWorkerTools.cs:49-54` vs `src/TradeAgent.AgentRuntime/RuntimeManifest.cs:461` `--dangerously-bypass-approvals-and-sandbox` |

## 4. Hard boundaries and limitations

| Boundary | Enforced / stated | Evidence |
|---|---|---|
| Operator authority off the agent pipe | enforced by absence | SOURCE `Protocol.cs:8-11` and the 24 op names at `:14-93` — no mode, kill switch, approval, allocation, verdict or update op; `TradingGateway.cs:122` says `Allocate` is in-process |
| Mutating ops role-gated | enforced, one place | SOURCE `GatewayPipeServer.cs:1154`; `Protocol.cs:95` `Mutating` |
| Holdout bars | enforced in the data layer | SOURCE `BarFeed.cs:132`, `Holdout.cs:79`, charge required `Referee.cs:188` |
| Allocation ceiling | enforced only for intents naming a version | SOURCE `TradingGateway.cs:1853-1869`; pipe-built intents name none (`GatewayPipeServer.cs:2825`), so an agent's `trade buy` today is un-attributed and un-ceilinged — it is gated by mode, role and the kill switch instead |
| Agent cannot edit measurements | enforced by absence of ops | SOURCE `Protocol.cs:35,41-47,64,75`; app-only writers `WorkspaceRevisions.cs:29-32` |
| OS isolation of agent code | **NONE — stated, not enforced** | SOURCE `src/TradeAgent.AgentRuntime/Containment.cs:43-46`: same user, can read/write TradeAgent's binaries and `state/`. Tool-level "cannot modify" is a contract, not a physical property |
| Armed-live + uncontained | enforced refusal to launch | SOURCE `Containment.cs:60-73` |
| Vendor CLI reach | unrestricted by design decision | SOURCE `RuntimeManifest.cs:461` and the comment at `:452-460` — sandbox modes unverified on Windows, so approvals and sandbox are bypassed |
| Market realism | fixture only | SOURCE `FakeBroker.cs:62-67` (deterministic, unmoving); data is completed monthly archives, SOURCE `src/TradeAgent.Core/Data/BinanceArchive.cs:16-21,30`. No advancing source exists in the tree |
| Protected evaluation vs mechanics-only | distinct and traceable | A protected evaluation needs a cutoff + open campaign + charged verdict (`SettingsView.cs:476`, `Referee.cs:188`); a FakeBroker run over fixed quotes has none of the three and must be labelled mechanics-only |
| No loop has ever been observed end to end | historical | HIST `BUILD-STATUS.md:2657-2659`, 2026-09-01: "Live money has never been touched"; "No agent has been asked to record its own work with `trade material` yet either." Scope: that date's units. Nothing in this survey was executed at runtime either |
| Conditions the next demonstration needs | — | Paper-only, no armed live authority (`Containment.cs:60`), a harness key (`AppHost.cs:125`), one dataset with a cutoff and an open campaign, and an advancing-price source that does not exist yet |

## 5. Smallest next slice

| Unit | Arrow closed | Acceptance evidence / prerequisite |
|---|---|---|
| `U-verdict-op` | candidate → bounded independent verdict — **the earliest broken dependency** | A harness turn asks for a verdict and receives `RefereeFeedback.Text`; a `promotion` row exists that no developer wrote; a second ask refuses on the campaign's verdict budget |
| `U-campaign-open` (may fold into the above) | backtest → a campaign that counts the trial | A trial registers with no owner press beyond the one-time holdout; the cutoff cannot move and the budget cannot renew |
| `U-paper-grant` | verdict → paper allocation without a press per version | An eligible version carries a standing paper allocation by app policy; `TradingGateway.cs:145` and the live path are untouched |
| `U-paper-runner` | deployment → forward intents through the existing gateway | Frozen version's `StrategyIntent` reaches `IntentDecision.From` and a `PlaceIntent` with app-owned execution identity and lineage; restart/replay duplicates no exposure; exits run as well as entries |
| `U-advancing-bars` | forward paper on advancing market data | The runner consumes a source whose prices advance; any FakeBroker run stays labelled mechanics-only |
| `U-decide-again` | fills/costs → another autonomous decision | A later turn reads `pnl`/`report`, records a reasoned keep/modify/kill/branch and schedules a wake or starts the next candidate, unprompted |
| Genuine owner-only prerequisites | — | The 12-month download (`SettingsView.cs:164`), one holdout cutoff press (`SettingsView.cs:476`), the harness key (`AppHost.cs:125`). Isolation is not a prerequisite for a paper-only run with no armed live authority, but it is one before claiming protected evidence (`Containment.cs:43`) |
