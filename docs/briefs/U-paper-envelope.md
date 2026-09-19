# U-paper-envelope — the owner grants bounded paper experimentation ONCE; eligible versions are then allocated to paper by app policy, never to live
**Arrow closed:** verdict → paper allocation with no press per version (`manager-prompt.md` § 5 "Paper authority"; `docs/PRINCIPLES.md` § boundary: "New live
authority and live capital allocations retain their deliberate owner confirmation"). **Observable result:** with a standing envelope, a `paper_eligible` or
`promoted` version receives a PAPER-scoped allocation the app writes; a dispatch in a LIVE mode never reads it; a paper dispatch on another connector, mode or
account never reads it; `TradingGateway.Allocate` (live, two-press) is untouched. **Schema 25** (after `U-forward-bars`' 24; you are cut from `U-paper-verdict`'s tip).
MONEY PATH: `AllocationCeilingOrThrow`, `Allocations.StandingFor` and every reader of `strategy_allocation`.
Read first: `docs/PRINCIPLES.md` §§ boundary, evidence; `docs/CONTRACTS.md` "The capital allocation" (2353-2421); `AllocationStore.cs:51-129,188-228,272-275`;
`TradingGateway.cs:145-162` (`Allocate`), `:1544-1546` (the PAPER-account check), `:1807-1821` (`AllocationFor`), `:1860-1898`, `:4117-4123`, `:4227-4259`;
`Trading.cs:227-360`; `DashboardView.cs:1303-1326` (the two-press allocate card), `Ui.cs:242-288`; `AppHost.cs:928-942`; `Database.cs:1118-1183` (rung 20 as the
pattern); `MissionLoop.cs:1783-1787` (`ApplyBoundaryDeadlines`, the app's periodic seam); `Referee.cs` `Deliver` (the wake-and-note pattern).
Items, one commit each with a one-sentence message:
1. `paper_envelope` (schema 24): one immutable row per grant — id (sha over its facts), connector_id, account_id, symbol, currency, max_quantity, max_notional?,
   max_deployments (1 for now), granted_at, expires_at, reason; `withdrawn_at` written only by a later withdrawal. Written ONLY by a two-press card on the
   Dashboard beside the allocate card ("Let TradeAgent run paper experiments on <account> for <symbol> up to <qty> / <notional> until <date>"), refused unless
   the account is provably simulated by BOTH sources (`AccountInfo.IsSimulated` AND `Capabilities.IsPaper` — stricter than `:1544`'s `||`, say why in the
   contract) and the gateway's mode is PAPER at the press. Widening is two presses; withdrawal one press plus confirm. `Envelopes.Standing(connector, account, now)`.
2. Allocation scope: `strategy_allocation` gains `scope` ('live'|'paper'), `connector_id`, `mode`, `account_id`, `envelope_id?` — NULL on existing rows and read as
   LIVE (an owner's press is never a paper grant); a paper row's id hashes the seven facts PLUS the scope facts (live ids unchanged, `IdOf` order preserved).
   `Allocations.StandingFor` becomes two readers: `StandingForLive(version, now)` (live rows only) and `StandingForPaper(version, connector, account, now)` (paper
   rows matching all three under a standing envelope). `Allocations.RecordPaper(...)` refuses unless `Standing(version).IsPaperEligible || IsPromoted`, the
   envelope stands, the ceiling ≤ the envelope's, and no other version holds a live-standing paper allocation on that envelope; `Record` (live) refuses paper scope.
3. Dispatch: `AllocationFor` takes the gateway's current (connector, mode, account): in PAPER mode only a paper row matching the pair it runs on authorises,
   otherwise `ALLOCATION_NONE`; in `LIVE_*` only live rows; a paper row authorises nothing in a live mode, categorically. The record's `allocation_id` is the
   matched row's. On an envelope's account, an AGENT order naming no version is refused `ENVELOPE_ACCOUNT_RESERVED` (the owner's orders and presses unaffected).
4. App policy: `TradingGateway.AllocatePaperDue(now)` — for each version standing `paper_eligible`/`promoted` with no paper allocation, when a standing envelope
   has room: write the paper allocation at the envelope's ceiling, reason "app policy: <verdict>", and one persisted wake to Research keyed by the allocation id
   with a sanitised note ("version X allocated to PAPER on <account> up to <qty>; no live authority; nothing runs it yet"). Called from the `ApplyBoundaryDeadlines`
   seam and after each verdict delivery. Report section 4 lists paper allocations apart from live ("PAPER — no live authority"); `PromotedLine` says it.
Red-first tests (Fault/Integration, the simulator, `TestEnv.Ready()`): (a) `A_paper_eligible_version_with_a_standing_envelope_is_allocated_to_paper_by_the_app_
with_no_press`; (b) `A_paper_allocation_never_authorises_a_live_dispatch` (LIVE_AUTONOMOUS, same connector and account → `ALLOCATION_NONE`); (c) `…never_
authorises_another_connector_mode_or_account` (three arms); (d) `The_live_press_still_refuses_a_paper_eligible_version`; (e) `An_expired_or_withdrawn_envelope_
allocates_nothing_new_and_its_rows_no_longer_authorise`; (f) `A_second_version_is_refused_while_max_deployments_is_full`; (g) `An_agent_order_naming_no_version_
on_the_envelope_account_is_refused`. Mutants to watch red and quote: (i) `AllocationFor` reading live rows in PAPER mode → (b)/(c) red; (ii) the scope facts
dropped from the paper id hash → (c) red. Existing exact column-list assertions extended and still exact; nothing removed.
Gate and report as `docs/HOW-WE-BUILD.md`: `--no-incremental` Release build 0 warnings; the three suites 0 failed; touched classes 3×; names vs `main` 0
removed; `## Report` ≤ 20 lines appended here. `CONTRACTS.md` "The capital allocation" amended; `USER-GUIDE.md` the card in one paragraph. No push, no merge.
