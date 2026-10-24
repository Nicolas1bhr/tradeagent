# U-amount-precision — a fee or slippage that would read as zero, or lose digits, is refused in words; and every box reopens showing text its own reader accepts, unchanged
**Source (seat B's card, due before M-org1):** the Codex second pass on U-number-entry-culture, `fleet/tmp/B-scratch/U-number-entry-culture-codex2/review.md` (P1
TryPercent, P2 AmountField redraw) and the earlier card U-percent-precision (PercentText's 12-place redraw). Pointers checked against main e317c011, 2026-10-24 — re-grep.
**Protections — EVID-001 (paper friction is an evidence setting, the owner's; a declared cost silently becoming zero changes what every paper result means),
refuse never guess (`src/TradeAgent.Core/Amount.cs:25`), the owner's number-entry rules of 2026-10-22 (culture-free; no change by scrolling an unclicked box) unchanged:
red-first and ONE watched mutant.** Seat B; **sonnet/high** (UI + its reader). **No rung.**
**Today:** `SettingsView.TryPercent` (`src/TradeAgent.App/SettingsView.cs:548`) checks precision before dividing by 100, so `"0.0000000000000000000000000001"` is accepted
as fraction 0. `PercentText` (`:605`) redraws a stored fraction ×100 at 12 places, so a stored fraction with more digits reopens as different text. `AmountField`
(`src/TradeAgent.App/AmountField.cs:38`, `:62`) seeds the box with `Amount.Exact(v)`; a saved `"1.2340"` reopens as `"1.234"`, which the parser (`Amount.cs:83`) can refuse as
ambiguous — the untouched box then has a null `Value` and blocks every save (the Oversight cap box and the Owner page's boxes use it).
**Observable result:** typing the 28-place fee is refused in words naming the smallest figure the box keeps; a stored fraction reopens as text that `TryPercent` reads
back to the same fraction (round trip exact, or the box says why it cannot); `"1.2340"` saved, the page recreated, the untouched box reads 1.234 with no refusal.
Items, one commit each, one-sentence messages:
1. **Refuse a percent that loses digits:** `TryPercent` refuses when `typed / 100` is not exactly representable (or rounds a positive figure to zero). Check:
   `A_fee_that_would_read_as_zero_is_refused_in_words` (`NumberEntryCultureTests` or the class the existing TryPercent tests live in).
2. **Exact percent redraw:** `PercentText` draws every digit the stored fraction holds ×100 (no 12-place cut). Check:
   `A_stored_fraction_reopens_as_text_that_reads_back_to_it`.
3. **Reopen unambiguous:** whatever `AmountField` draws for a value, its own reader accepts as that value (draw a form the parser never calls ambiguous, e.g. keep a
   trailing digit or the original text). Check: `An_untouched_box_reopened_after_1_2340_reads_1_234_without_refusal` — covering the Oversight cap's delayed initialisation.
Red-first (quote each red at base): items 1–3's checks.
**The watched mutant:** item 1's guard removed (precision checked only before the division) ⇒ item 1's test red. Quote, restore, green.
**Read first:** CLAUDE.md (Conventions: Theme.cs); the review file; `Amount.cs`, `AmountField.cs`, `SettingsView.cs:480-610`, `OversightView.cs:270-310`.
**NOT in scope:** what the boxes govern (R24 moved limits to the board; these are the owner's evidence settings and caps that stay his); any new box; the Mac UI check.
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; branch CI on three platforms; Codex review; names vs
`main` 0 removed; tests box line). No app launch or screenshot: the owner is at the Mac. `## Report` ≤ 20 lines appended here.
