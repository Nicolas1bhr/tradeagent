# U-org-rights — a position may backtest, ask a verdict and propose a lens, never trade unless it is `operations`, and its output is held, not routed to Research
**Arrow closed:** fixed roles → positions, second half: rights and the relay (`docs/ORCHESTRATION.md` §§ 2.1 rules, 2.3, 2.10, § 10's table; `docs/ORGANISATION.md` §§ 2–3, 13; R12 § 5 O2;
R18 § 4; R17 #3). **Protects:** order permission (ORDER-001, ORCHESTRATION § 8: `MayPlaceOrders` unchanged, never by headship; `Council.cs:63`) — red-first and a mutant; ORG-001.
**Depends on `U-org-principals`** (its active-position helper, item 3, and its seam, item 4). Dispatch ∥ `U-org-envelopes` (ORGANISATION § 15, lane B). No schema change. Opus, high.
**Rights are not runnability:** this unit says what an ACTIVE position may do WHEN it runs; whether it runs is the activation gate's (`U-thinking-seats`; until it exists O2a's
`PositionActivation.Refusal` refuses every non-legacy launch). It adds no organisational verb — decision verbs on an unconfined seat stay refused (§ 2.3) — and grants nothing to the legacy pair.
**Today (SOURCE at `9b6298db`; code unchanged since `fd7174ee`, re-grepped at `3cfb6ef7`; NOT runtime-verified):** "may backtest or ask a verdict" is `CouncilRoles.IsKnown` through `Backtests.RoleOf`
(`Gateway/Backtests.cs:311-319`, called at `:102` and by `VerdictFor` `GatewayPipeServer.cs:2684-2689`), and `Resolve` reads the program from `Paths.RoleHome(role)` (`Backtests.cs:338`);
`Holdout.Pipe` uses it for the refusal's wording only (`Core/Data/Holdout.cs:72-73`); lens-propose is gated by `IsKnown` too (`GatewayPipeServer.cs:4024-4028`). `MayPlaceOrders` is
`role == "operations"` (`Council.cs:63`; `GatewayTypes.cs:271`; the canon's reach `AgentReach.cs:98, 120`, `Canon.cs:833`) and gates every mutating op (`GatewayPipeServer.cs:1188`). The relay
routes any non-Research output to Research as an agenda (`KindFor`/`KindsFor`/`RecipientsOf`, `CouncilRelay.cs:596-597, 608-609, 616-617`; patterns `:620-626`); `Reconcile` walks the legacy pair
(`:250`, kept there by O2a); `Fence` already compares the exact role (`:497-511`, since U-decision-port). `RoleObjectives.KindsOf`/`Open` give any other role `[]` (`RoleObjectives.cs:77-82,
95-140`), so the loop withdraws its looks (`MissionLoop.cs:2040-2054`). `OrgStore` has NO writer (`OrgStore.cs:160-168`): ids are minted by `U-org-verbs`, so the minting RULE lands here as code.
**Observable result:** an active position may backtest (its program read from its own home, `org/<id>`), ask a verdict and propose a lens; app principals (`referee`, `allocator`, `perception`,
`OrgStore.IsAppPrincipal` `:278`) never become positions; no position but `operations` may place, change or cancel an order — headship included; a minted id never equals a legacy id, an app
principal or a Windows reserved device name; a non-legacy position's output is held with a disposition in its next Situation (or packet, as `U-reconcile-packet` lands it) — "no recipient until
U-org-assignments" — never routed to Research; it holds no objective, so no look wakes it (its clock is O3's); legacy routing and the legacy pair's every test are unchanged.
Read first: `CLAUDE.md`; `docs/ORCHESTRATION.md` §§ 2.1, 2.3, 2.9, 2.10, 8; `docs/ORGANISATION.md` §§ 2–3 ("`operations` is the owner's"), 13; R12 § 1 seams 3–4, 17; R18 § 1.3 rows 2, 6;
`CouncilRelay.cs:240-330, 480-630`; `Backtests.cs:95-110, 295-345`; `GatewayPipeServer.cs:1175-1200, 2680-2700, 4015-4040`; `Holdout.cs:60-75`; `RoleObjectives.cs:70-140`; `OrgStore` and O2a's helper.
Items, one commit each, one-sentence messages:
1. Rights through O2a's helper: `Backtests.RoleOf` (both callers) answers an ACTIVE position or the existing refusal; app principals keep their own paths; `Resolve` reads the position's own home;
   `Holdout.Pipe`'s refusal names an active position by its id instead of "a caller that proved no role"; `LensPropose`'s `IsKnown` (`:4024`) takes the same helper.
2. `MayPlaceOrders` stays `role == "operations"` — unchanged at every site above, never derived from headship or from `OrgStore`; `OrgIds` gains the minting rule (app-random, under a prefix no
   legacy id, app principal or device name can take) and ONE validator refusing any id equal to a legacy id, an app principal or a Windows reserved device name — the check `U-org-verbs`' writers call.
3. `CouncilRelay`: `Reconcile` walks the active positions; output from a non-legacy position passes `Fence` as today and is then HELD — published to nobody — with its disposition in the writer's
   next Situation; `KindFor`/`KindsFor`/`RecipientsOf` route the legacy pair only, and recipient homes (`:579`) stay theirs; the hold is a disposition in the existing vocabulary
   (`MissionEventDisposition`, `MissionEventStore.cs:106`) or a new constant of it.
4. `RoleObjectives`: `KindsOf`/`Open` stay the legacy pair's; a GUARD pins that a non-legacy position holds none (its objectives are mandates, `U-org-assignments`; its schedule, O3).
Red-first tests: (c) `A_third_position_may_backtest_and_is_refused_orders` — the third position HEADS a test-seeded team under `div-research`, and `research` (head of `div-research`) is asserted
refused too; (d) `A_third_positions_output_is_never_routed_to_research` (quoted red at base: `Run("<id>")` lands it as Research's agenda); (f)
`A_minted_position_id_never_equals_a_legacy_id_an_app_principal_or_a_device_name`; (g) `A_third_position_may_propose_a_lens_and_an_app_principal_may_not` (red at base: "presented no launch grant",
`:4026`); (i) GUARD `A_third_position_holds_no_objective_so_no_look_wakes_it`. Mutants to watch red and quote: `MayPlaceOrders` by headship ⇒ (c); `RecipientsOf`'s else-branch serving a
non-legacy position ⇒ (d).
Conflicts (re-check at dispatch): `U-org-envelopes` (∥) shares `AiAttemptStore.Refuse` (`:288-308`) only if this unit touches it — it must not; `U-evidence-identity` landed — `VerdictFor` is now
`:2684` with `RoleOf` at `:2689`, nothing left to collide; `U-reconcile-packet` (in flight: the Situation's composition in `MissionLoop.cs`) — M, rebase and put the hold line where it composes.
Gate: SPEED MODE (`fleet/SPEED-MODE.md` § 4) — rebase on `main` first; Release `--no-incremental` 0 warnings; touched classes 3×; the full suite on branch CI, all three platforms; tests box `ready`
once or NOT RUN; names vs `main` 0 removed (both set sizes printed). `## Report` ≤ 20 lines appended here. No push to `main`, no merge.
