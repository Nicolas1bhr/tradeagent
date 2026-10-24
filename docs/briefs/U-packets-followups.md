# U-packets-followups — a malformed forecast row is refused by name instead of dropping the org block, and an attempt's input lineage records every ledger entry and failed delivery its packet showed
**Source (seat B's card, due before M-org1):** the Codex second pass on U-org-packets-b, `fleet/tmp/B-scratch/U-org-packets-b-codex2/review.md` — its P2 (OrgStore.Packets
forecast rows) and its first P1 (MissionLoop lineage). Its forecast-vocabulary P1 was folded into U-org-verbs-c — verify it on main and say so; its ApiConversation P1
was triaged not kept (BOARD 01:02, 2026-10-23) and is NOT in scope. Pointers checked against main e317c011, 2026-10-24 — re-grep.
**Protections — EVID-001 (what each attempt received is evidence: `ai_attempt_input` must name everything the prompt showed, no more, no less), refuse never guess
(one bad row is refused by name, the rest still shown): red-first and ONE watched mutant.** Seat B; **opus/high** (evidence). **No rung.**
**Today:** `OrgStore.Packets.cs` reads forecast rows in `ResolutionsUnshown` (`:82-104`) and `Forecasts` (`:108-116`) with no per-row exception handling: one malformed
`at` throws through the list and `PacketOf` discards the whole org block (valid notes and deliveries included). The packet's inputs (`MissionLoop.cs:3106`, recorded at
`:3947` via `RecordInputs`) take only `org.Shown` and a rejection event; the ledger entries rendered in the `Believed` block (`:1072`, `:1285`, `:3114`) and the failed
deliveries shown in the mandate block are not recorded.
**Observable result:** a forecast with a valid probability and an invalid `at`, beside a valid note, yields the note and a named refusal (the row's id), not a throw; an
executor turn whose prompt shows one belief and no org records commits an attempt whose `ai_attempt_input` names that ledger entry; a failed delivery shown in the
mandate block is named in the lineage too.
Items, one commit each, one-sentence messages:
1. **Per-row refusal:** both readers catch a row's parse failure into the list's `refused` with the row id; nothing else changes. Check:
   `A_malformed_forecast_row_is_refused_by_name_and_the_org_block_survives` (`OrgPacketTests`).
2. **Lineage of beliefs:** every ledger entry rendered in `Believed` is a `PacketInput` of the attempt. Check: `A_turn_shown_one_belief_records_it_as_an_input`.
3. **Lineage of failed deliveries:** every failed delivery the mandate block shows is a `PacketInput`. Check: `A_failed_delivery_shown_in_the_mandate_block_is_recorded`.
Red-first (quote each red at base): items 1–3's checks.
**The watched mutant:** item 2's inputs built from `org.Shown` alone again ⇒ item 2's test red. Quote, restore, green.
**Read first:** CLAUDE.md (measurement vs claim); the review file; `OrgStore.Packets.cs`; `MissionLoop.cs:1060-1300, 3090-3130, 3930-3960`; the input kinds `PacketInput` admits.
**NOT in scope:** the packet's size bounds and rendering; ApiConversation; forecast resolution semantics.
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; branch CI on three platforms; Codex review; names vs
`main` 0 removed; tests box line). `## Report` ≤ 20 lines appended here.

## Report
Code tip 28c35980 on base bb4f177e (rebase: up to date); no rung; no deviation from the brief.
- Build: `dotnet build TradeAgent.sln -c Release --no-incremental` → "0 Warning(s)", "0 Error(s)".
- Touched classes 3× in Release (suite lock): OrgPacketTests "Passed! - Failed: 0, Passed: 12" ×3; CouncilLoopTests "Passed! - Failed: 0, Passed: 78" ×3 (PuppetLivesTests, which reads lineage too: "Passed: 17").
- CI 40113759887 on 28c35980: conclusion success, 12 of 12 jobs green.
- Codex review (gpt-6.1-sol, read-only): `fleet/tmp/B-scratch/u-packets-followups/U-packets-followups-review.md` — "No defects found".
- names.sh main u-packets-followups: "sets: base 3341 tip 3344", "removed: 0", "added: 3".
- tests box: NOT RUN — the machine does not answer
- Item 1 (b7274253): `ForecastsDue`/`ResolutionsUnshown` catch a row's read failure (Format/InvalidCast/Overflow/Argument) into `refused` by forecast id, never its text; check `A_malformed_forecast_row_is_refused_by_name_and_the_org_block_survives`.
- Item 2 (f57fbee7): `MissionSituation.Lineage` adds a `ledger` input for every entry "What you believe" renders, through `BeliefLines` — the one cut the render also uses; check `A_turn_shown_one_belief_records_it_as_an_input`.
- Item 3 (28c35980): `Lineage` adds a `delivery` input for the delivery the mandate block shows as failed, through `FailedShown` — the render's own test; check `A_failed_delivery_shown_in_the_mandate_block_is_recorded`.
- RED at base: item 1 "System.FormatException : The string 'not a time' was not recognized as a valid DateTime" (thrown out of `ForecastsDue`); item 2 "Expected: ["ledger:1"] Actual: []"; item 3 "Expected: ["delivery:666548bf…"] Actual: []".
- MUTANT (item 2's beliefs dropped from `Lineage`, inputs from `org.Shown` alone again): "Expected: ["ledger:1"] Actual: []" — red; restored → "Passed: 1".
- Forecast-vocabulary refusal on main: yes — `OrgStore.VocabularyRefusal` (`src/TradeAgent.Core/Db/OrgStore.Packets.cs`), called first in both readers.
- NOT done/verified: no full local suite; the other forecast readers (`OrgStore.Surface.cs`, `OrgStore.Decisions.cs`) are out of scope and unchanged; a rejected delivery named only by id in the rejection line is recorded through its mandate event, as before, not as a `delivery` input.
