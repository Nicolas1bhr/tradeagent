# U-org-rights — a position may backtest and ask a verdict, never trade unless it is `operations`, and its output is held, not routed to Research
**Arrow closed:** fixed roles → positions, second half: rights and the relay (`docs/ORGANISATION.md` §§ 2–3, 13; R12 § 5 O2; R18 § 4; R17 #3). **Protects:**
order permission (`CLAUDE.md`; `Council.cs:63`) — red-first and a mutant. **Depends on `U-org-principals`.** No schema change. **Today (SOURCE at `1275aff`,
R12/R18-checked, NOT runtime-verified):** "may backtest or ask a verdict" is `CouncilRoles.IsKnown` through `Backtests.RoleOf` (`Backtests.cs:209-216`, called at
`:92` and `GatewayPipeServer.cs:2454`); `Holdout.cs:68, 73` uses it for the refusal's wording only; `MayPlaceOrders` is `role == "operations"` (`Council.cs:63`;
`GatewayTypes.cs:270`) and gates every mutating op (`GatewayPipeServer.cs:1179-1188`); the relay routes any non-Research output to Research as an agenda
(`CouncilRelay.cs:587-608`), so a third position's file passes `Fence` and lands on Research's desk. **Observable result:** an active position may backtest and
ask a verdict; app principals (`referee`, `allocator`, the queued `perception`) never become positions; no position but `operations` may place, change or cancel
an order — headship included; a minted id never equals a legacy id; a non-legacy position's output is held with a disposition in its next Situation ("no
recipient until U-org-assignments"), never routed to Research; legacy routing and the legacy pair's every test are unchanged.
Read first: `CLAUDE.md`; `docs/ORGANISATION.md` §§ 2–3 ("`operations` is the owner's"), 13; R12 § 1 seams 3–4, 17; R18 § 1.3 rows 2, 6; `CouncilRelay.cs:480-620`;
`Backtests.cs:80-100, 200-220`; `GatewayPipeServer.cs:1170-1195, 2440-2500`; `Holdout.cs:60-80`; `OrgStore` as landed.
Items, one commit each, one-sentence messages:
1. `Backtests.RoleOf` (two callers; it may change): an ACTIVE position or nothing; app principals keep their own paths; `Holdout`'s refusal names an active
   position by its id instead of calling it "a caller that proved no role".
2. `MayPlaceOrders` stays `role == "operations"` — unchanged, never derived from headship or from `OrgStore`; `OrgStore` refuses to mint any id equal to a
   legacy id or an app principal.
3. `CouncilRelay`: output from a non-legacy position is held with its disposition in the writer's next Situation; `KindFor`/`RecipientsOf` route by the legacy
   pair only; the hold is a disposition in the existing vocabulary or a new constant of it.
Red-first tests: (c) `A_third_position_may_backtest_and_is_refused_orders` — the third position HEADS a test-seeded team under `div-research`, and `research`
(head of `div-research`) is asserted refused too; (d) `A_third_positions_output_is_never_routed_to_research` (quoted red at base: it lands as Research's agenda);
(f) `A_minted_position_id_never_equals_a_legacy_id_or_an_app_principal`. Mutant to watch red and quote: `MayPlaceOrders` by headship ⇒ (c) red.
Conflicts (R18 § 3, re-check at dispatch): `U-org-envelopes` (W6, same wave as this light leg) shares `AiAttemptStore.Refuse` (`:288-305`) only if this unit
touches it — it should not; `U-evidence-identity` (landed) edits `GatewayPipeServer.VerdictFor` at `:2484` against this unit's `:2454` (M — re-check).
Gate and report per `docs/HOW-WE-BUILD.md`: rebase on `main` first; `--no-incremental` Release build 0 warnings; three suites 0 failed; touched classes 3×;
names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push, no merge; touch nothing in `docs/briefs/` but this file.
