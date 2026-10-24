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

## Report
Code tip dd41c6d4 (three item commits 9d6ab0f9, f038f640, dd41c6d4 on base bb4f177e); this report is the commit after it.
- Gate 1: `dotnet build TradeAgent.sln -c Release --no-incremental` -> 0 Warning(s), 0 Error(s).
- Gate 2: PaperFrictionTests + NumberEntryCultureTests + OversightPageTests (32 tests), 3x Release via suite.sh -> Passed 32/32 each time.
- Gate 3: CI run 40113558993 on dd41c6d4: GREEN (every job, per seat B). Codex review (`fleet/tmp/B-scratch/U-amount-precision/U-amount-precision-review.md`): "No defects found".
- Gate 4: names.sh main u-amount-precision -> "removed: 0"; [Fact]/[Theory] base 3153, tip 3156 (+3).
- Gate 5: tests box: `ready` exit 0 and `start` ran, but the Mac restarted mid-wait; NOT VERIFIED on the tests box for this tree (its latest recorded run is another unit's).
- Item 1 (9d6ab0f9): `TryPercent` refuses when `typed/100*100 != typed`, naming the smallest figure (0.00000000000000000000000001%) via `Labels.PercentTooPrecise`.
- Item 2 (f038f640): `PercentText` is `Amount.Draw(f*100m)` (every digit, no 12-place cut; 1.234 drawn as 1.2340). New `Amount.Draw` shares `LooksGrouped` with the reader.
- Item 3 (dd41c6d4): `AmountField` ctor and `OpenOn` draw with `Amount.Draw`, so a saved 1.2340 reopens as "1.2340" and reads 1.234 with no refusal (Oversight cap included, early and late open).
- RED at base (before each fix): A_fee_that_would_read_as_zero -> "Assert.False() Failure, Expected: False, Actual: True"; A_stored_fraction_reopens -> "Expected: 0.0000123456789012345, Actual: 0.0000123456789"; An_untouched_box_reopened -> "Expected Tuple (1.234, null), Actual Tuple (null, '1.234 could be 1234 or 1.234...')". Item 3's red ran with `Draw` stubbed as `Exact` (compile only).
- MUTANT: item 1's guard made `if (false && kept * 100m != percent)` -> A_fee_that_would_read_as_zero RED (Failed 1, Passed 10); restored -> Passed 11/11.
- Deviation: the brief's "or the box says why it cannot" case does not exist: a fraction holds at most 28 places, so its percentage holds at most 26 and always divides back exactly; the finest fractions are in the test table instead.
- Not done: no app launch, no screenshot, no full local suite; the 12-place cut is gone everywhere `PercentText` draws, but other `Amount.Exact` uses (readings, notes) are unchanged by design.
