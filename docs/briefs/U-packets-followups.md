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
