# U-verbs-a-followups — org-show marks a turn charged its reservation because nothing was measured, every open mandate is reachable through a continuation, and `trade org show` refuses arguments it does not take
**Source (seat B's card, due before M-org1; held behind U-org-verbs-e, which touched the same files):** the Codex second pass on U-org-verbs-a, `fleet/tmp/B-scratch/U-org-verbs-a-codex2/review.md`
(P1, P2, P3). Pointers re-grepped at dispatch against main — re-grep again; U-org-verbs-e reshaped org-show (three dimensions) and `UnitDay`.
**Protections — measurement vs claim (an unmeasured cost is never shown as a priced one; BUDGET-001 "unknown never zero"), refuse never guess (a CLI argument that is
not taken is refused, never silently dropped; a cut list says it is cut and how to continue): red-first and ONE watched mutant.** Seat B; **opus/high** (spend
measurement). **No rung.**
**Today:** `TurnMeter.EnvelopesFor` (`src/TradeAgent.AgentRuntime/TurnMeter.cs` ~:1600) builds `UnitDay` (`src/TradeAgent.Core/Db/OrgStore.cs:222` — `Spent, Reserved, Unpriced`)
without the day's UNREPORTED turns (the meter's `totals.Unreported`, ~:1702), so org-show shows a LOST or no-usage turn charged its reservation as priced spend.
`OrgShow` and `MandateList` (`src/TradeAgent.Gateway/GatewayPipeServer.Mandates.cs` :252, :201) cut at `MandateListMax = 50` (:193) with no continuation, so older open
work cannot be enumerated. `trade org show` (`src/TradeAgent.TradeCli/Program.cs` ~:420) ignores extra positional arguments.
**Observable result:** one funded turn ended without usage → org-show names its reservation charge as unmeasured (count and amount), apart from priced spend; 51 open
mandates for one caller → every id reachable through `org-show`/`mandate-list` with a continuation (a cursor the reply names, and a line saying the list is cut);
`trade org show pos-other` exits non-zero with a refusal naming the argument.
Items, one commit each, one-sentence messages:
1. **Unmeasured shown:** `UnitDay` carries the unreported turns' count and their reservation-charged amount; org-show renders them as unmeasured, never inside priced
   spend. Check: `A_turn_ended_without_usage_shows_as_a_reservation_charge_not_priced_spend`.
2. **Continuation:** both readers return at most 50 rows AND a cursor (the last row's stable order key) when more exist; the request accepts that cursor; the reply
   says the list is cut. Schema + CLI flag. Check: `Fifty_one_open_mandates_are_all_reachable_through_the_continuation` (both ops).
3. **CLI refuses extra arguments:** Check: `Org_show_with_an_argument_is_refused`.
Red-first (quote each red at base): items 1–3's checks.
**The watched mutant:** item 1's unreported amount folded back into `Spent` ⇒ item 1's test red. Quote, restore, green.
**Read first:** CLAUDE.md (measurement vs claim); the review file; `TurnMeter.cs:1560-1720`; `GatewayPipeServer.Mandates.cs:190-330`; `GatewaySchema.cs` rows for both ops;
`Program.cs` `org` and `mandate` verbs.
**NOT in scope:** the meter's pricing; any other reader; the Org page (it reads the same `UnitDay` — if it renders `Spent`, show the unmeasured part beside it in words, Theme.cs only).
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; branch CI on three platforms; Codex review; names vs
`main` 0 removed; tests box line). `## Report` ≤ 20 lines appended here.
