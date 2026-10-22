# U-number-entry-culture — every number the owner types on a money or limit surface is read ONE stated way, culture-free: a point or a comma is the decimal mark, nothing else is; what cannot be read unambiguously is refused in words naming the form, never reset or rescaled, and the read figure is on screen before any press
**Arrow closed:** the owner's live defect of 2026-10-22 (Mac, region Belgium, `AppleLocale en_US@rg=bezzzz`): the Safety page's "The most any one of them may
hold at once" read his "0,05" as **5** (a 100× larger position cap) and drew his "0.05" back as **0**. CLAUDE.md: "Anything that moves money or removes
permission is two-press", "the dashboard tree is built once and updated in place", "Refuse, never guess". Survey: `fleet/tmp/B-scratch/s-number-entry-culture/survey.md`
(main `a42969c1` + u-billing-classes `214128c2`; the measured culture table and the NumericUpDown probe are in its `check/`) — re-grep every pointer after U-billing-classes lands.
**Protections — the two-press rule (a widening/raise/grant still asks twice and its armed sentence names the figure that will be written, exactly), refuse never
guess (unreadable or ambiguous input is a refusal, never 0, never the saved figure, never a rescale), the dashboard tree built once (no refresh writes a box being
edited; the reading line is updated in place), AUTH-001 (nothing here reaches the pipe — operator settings stay in-process): red-first and ONE watched mutant.**
Seat B; **opus/high** (every money and limit box on the Safety page). **No rung** (no schema, no setting changes meaning).
**Depends:** `U-billing-classes` LANDED first (it adds `_planCap` and rewrites the Safety page's cost section).
**The reading, decided (recommendation; seat B rules):** culture-FREE, not the user's culture. The build is `InvariantGlobalization=true` (`Directory.Build.props:7`),
so the app cannot even see a culture without a build-wide ICU switch; the owner's Mac reports a mixed locale (US language, Belgian region — this Mac's ICU resolves
it to en-BE, comma decimal); the Windows laptop's culture is unknown; and the owner typed BOTH "0,05" and "0.05" within minutes — any single culture misreads one of
them (invariant/en-US/en-GB: "0,05" = 5; de-BE/nl-BE/de-DE/en-BE: "0.05" = 5; fr-BE refuses "0.05"). One rule that needs no culture reads both as 0.05 and says so.
The rule: digits, optionally ONE decimal mark — `.` or `,` — and digits; no thousands separators, no sign, exponent, currency, `%` or inner spaces (outer spaces
trimmed). REFUSED as ambiguous: a single mark followed by exactly three digits after an integer part of 1–3 digits not starting with 0 ("1.000", "1,000", "12,500"
— a thousand or one?). Whole-number fields (positions, orders a minute, days, lanes, usage counts) refuse any mark. Empty is refused on a money box (write 0).
**Today:** every box is Avalonia 12.1.1 `NumericUpDown` from one factory, `Ui.NumberField` (`App/Ui.cs:600-605`), with Avalonia's defaults `NumberStyles.Any` +
`NumberFormatInfo.CurrentInfo` (= invariant: `.` decimal, `,` group) — "0,05"→5, "1e3"→1000; `FormatString "0"` for increment-1 boxes redraws 0.05 as "0" and
0.5 as "1" (`_envelope*`/`_allocation*` `DashboardView.cs:1173-1174,1188-1190`, `_maxQty :1675`); an unreadable text is silently rewritten to the previous value
(Avalonia `SyncTextAndValueProperties`, measured); an empty box is null and the readers fall back to `?? 0m` (`ReadAllocation :2686-2689`, `ReadEnvelope :2751-2756` —
days → an envelope ending now) or `?? current` (`PendingCap :2422`, `PendingRate :2443-2447`, `PendingLimits :2580-2597`, `BuildSaveDailyCap :1354`, branch
`_planCap :1400,2585`, `PerceptionCard.cs:500`). Paper fee/slippage (`SettingsView.cs:513-538`, `TryPercent`) read a comma as the decimal point and "1,000" as 1.
Armed money sentences go through `Labels.Money` "0.####" (`Errors.cs:753-754`), which rounds past 4 dp; quantities through `AllocationRow.Num` (exact).
**Observable result:** on the Safety page, typing "0,05" or "0.05" into "The most any one of them may hold at once" shows the box's reading line "reads as 0.05" and
the armed grant names 0.05; "1.000" or "1,000" shows a refusal naming the form ("“1.000” could be one thousand or one: write 1000 or 1") and the press does
nothing but say it; "0,05" in "Most open positions" says "a whole number"; the typed text is never rewritten by the box, a stepper, a commit or the refresh; an
empty box refuses the press instead of saving 0 or the old figure. Same on the perception budget and the paper fee/slippage boxes.
Items, one commit each, one-sentence messages:
1. **The one reader** in Core (`Core/Amount.cs`, `Amount.TryRead(string? text, AmountKind kind, out decimal value, out string? why)`; `kind` = decimal or whole):
   the rule above, culture-free by construction (no `CultureInfo.CurrentCulture` reached — parse the normalised digits with `InvariantCulture` and
   `NumberStyles.AllowDecimalPoint` only), refusal sentences in `Labels` naming the typed text and the expected form; and `Amount.Exact(decimal)` (the
   `AllocationRow.Num` digits) as the one way a read figure is printed back. Check: `The_owners_strings_read_one_way_under_every_culture` — "0,05", "0.05", "5", "1.000",
   "1,000", "0,5", "12.5", "1e3", "-1", "1.000,5", "", "0.000" under the thread culture set to clones of invariant carrying de-BE, fr-BE (group U+202F), nl-BE,
   de-DE, en-GB, en-US separators and invariant (named cultures cannot be built under `InvariantGlobalization`; the clone is `CoreTests.cs:364-366`'s precedent).
2. **The one field** (`Ui.AmountField(decimal value, AmountKind kind)`, code-built, Theme values only): a text box that keeps exactly what was typed, with a
   live reading line under it (`Theme.Small`/`TextMuted`, refusal in `Theme.Caution`) that says "reads as X" or the refusal; exposes `decimal? Value` (null while
   refused or empty) and `string? Refusal`, raises `ValueChanged` only when the read figure changes; it never writes its own text after construction (no
   NumericUpDown text round-trip, no steppers, no FormatString). Check: `A_typed_amount_is_never_rewritten_or_rescaled` (type "0.05": text stays "0.05",
   Value 0.05; type "0,05": same; type "abc": text stays, Value null, refusal shown).
3. **Every money and limit box moves to it** — envelope ×3, allocation ×2, the nine `RiskPolicy` boxes, daily cap, plan capacity, allowance ×2, price ×2,
   research share, perception budget, and `TryPercent` routed through `Amount.TryRead` (its `%` and max checks kept); `Ui.NumberField` deleted if nothing is left
   on it (lanes and review-every move too: one parser on the page). Check: `Every_number_box_on_the_safety_page_is_an_amount_field` (walks the built tree: no
   `NumericUpDown` left) and red-first `The_owners_comma_reads_as_five_hundredths_in_the_envelope_box` (base: typed "0,05" through the box's own commit reads 5).
4. **Refused or empty never presses through:** every reader that fell back (`?? 0m`, `?? current`) instead refuses — the press does nothing but write the box's
   refusal in its note line, on either press of a two-press; a box edited under an armed press disarms it (kept). Check: `An_unreadable_box_refuses_the_press_never_saves_a_fallback`
   (envelope days empty → no grant; daily cap "1,000" → nothing saved, note names the form).
5. **The figure is shown before the press:** every armed sentence over these boxes prints the read figure with `Amount.Exact` (money with its currency, never
   rounded), and a one-press save's note names the exact figure written. Check: `The_armed_grant_names_the_exact_figure_typed` ("0,05" → the armed envelope
   sentence contains "0.05"; cap "0.00005" → the raise sentence names 0.00005, not 0.0001).
6. **No refresh writes a box:** a guard that the page's refresh paths never assign an `AmountField`'s text or value after construction. Check:
   `A_refresh_never_rewrites_a_box_being_edited` (type into the cap box, run `Refresh` and the five-second tick's page refresh, the text is unchanged).
7. **No scroll changes a number (the owner, 2026-10-22 19:1x, verbatim: "it's also important that values cannot be changed by scrolling over them if
   they weren't clicked in the first place").** A mouse wheel or trackpad scroll over a money or limit box the owner has NOT clicked into (not focused) never
   changes its value or its text — the event is left unhandled so the page scrolls; the shared factory carries it (`Ui.NumberField` today, `Ui.AmountField` once
   item 2 replaces it), so every money and limit box inherits it. **Decided: a FOCUSED box takes no wheel change either** — on money and limit fields a wheel
   step is a silent change the owner did not type, made by a gesture that also means "scroll the page"; typing is the one way a figure changes, and it is
   refused or shown before any press (items 1–5). Check, red-first through the CURRENT `NumericUpDown` (a `PointerWheelChanged` raised over the box, unfocused,
   then focused): `A_scroll_over_a_money_box_never_changes_its_value_and_scrolls_the_page` — Value and Text unchanged, the event not marked handled; a case
   green at base is named a guard in the report, the red one quoted.
Red-first (quote each red at base, typing through the CURRENT `NumericUpDown`'s own commit — `Text` then a `KeyDown` Enter, as the owner does): item 3's
"0,05" → 5 and item 2's "0.05" redrawn as "0", item 4's empty envelope days → a grant until now, plus item 1's checks under de-BE, fr-BE and nl-BE.
**The watched mutant:** in `Amount.TryRead` the comma branch is deleted so `,` falls through to `NumberStyles.Number` under the invariant culture (a group
separator — today's reading) ⇒ item 1's de-BE/fr-BE/nl-BE rows and item 3's red-first go red on "0,05" = 5. Quote, restore, green.
**Read first:** CLAUDE.md (Conventions; Refuse, never guess); the survey and its culture table; `App/Ui.cs:220-410,560-610`; `DashboardView.cs` the Safety page
(`:1025-1960`, `:2390-2600`, `:2680-2870`); `PerceptionCard.cs:150-260,480-510`; `SettingsView.cs:460-545`; `Protocol.cs:305-350` (`Dec`, the strict precedent);
`CoreTests.cs:340-380`; the tests that set `box.Value` (`MissionCostSurfacesTests.cs:228-255`, `OrgLanesTests.cs:660-670`, `PerceptionCardTests.cs:245-270`,
`LossBudgetSurfacesTests.cs:255-265`) — rewrite them to type text; the report names each.
**NOT in scope:** turning `InvariantGlobalization` off or reading the OS culture; any output formatting outside these presses' sentences and notes (`Labels.Money`
elsewhere, reports, the daily report); the pipe's `Dec` (already strict); date entry (`SettingsView.cs:835`); new limits, ranges or defaults; steppers.
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; branch CI on three platforms; Codex review; names vs
`main` 0 removed — tests rewritten under item 3 are named; tests box line; a `tools/mac-shot.sh` of the Safety page with "0,05", "0.05" and "1.000" typed).
`## Report` ≤ 20 lines appended here.

## Report
Code tip `630cad3e` (base 988c9487, 7 item commits). Gate: build Release --no-incremental "0 Warning(s) 0 Error(s)"; touched classes (NumberEntryCulture, BillingClasses, MissionCostSurfaces, OrgLanes, PerceptionCard, LossBudgetSurfaces, PaperFriction, TwoPressGrant, BudgetReservation, PaperEnvelope) 3× "Passed! Failed: 0, Passed: 161"; CI 39827423607 success, 12/12 jobs; names.sh "removed: 0, added: 10" (2965 → 2975).
RED at base through the stock box's own commit (Text then Enter): item1 de-BE/fr-BE/nl-BE clones "0.05->5" all three; item3 envelope "0,05" Expected 0.05 Actual 5; item2 "0.05" then the leave-the-box commit Expected "0.05" Actual "0"; item4 empty days read an envelope until now (until = now + 0 ms); item7 focused wheel Expected (7, False) Actual (8, True), the unfocused case already green at base (a guard).
MUTANT (in Amount.TryRead, the comma branch replaced by today's reading, NumberStyles.Number invariant): 6 of 10 red — "under de-BE, “0,05” read as “5”, not “0.05”", envelope Expected 0.05 Actual 5. Restored, green.
1 done: `Core/Amount.cs` TryRead/Exact/ExactMoney, refusal sentences in Labels. Deviation: the ambiguous refusal names numerals ("could be 1000 or 1: write 1000 or 1"), not "one thousand or one". The test clones also set the currency and percent separators, because NumberStyles.Any reads those too and an invariant currency mark let "0.05" through.
2 done: `App/AmountField.cs` + `Ui.AmountField(value, kind, min, max)`, reading line Small/TextMuted, refusal in Caution, `Theme.FieldWidth` added. It reads on the TextProperty change, because TextChanged did not fire without a visual tree.
3 done: all 26 boxes plus `TryPercent`. Whole kind: positions, orders a minute, strike-window days, envelope days, tokens, review minutes, lanes (1..MaxLanes), research share (0..100). `Ui.NumberField` and the NumericUpDown styles are deleted. Tests rewritten to type text: MissionCostSurfaces, OrgLanes, BillingClasses, PerceptionCard, LossBudgetSurfaces (source strings now read .Figure). TwoPressGrant's source assert now includes `LimitsRefusal,`. BudgetReservation's PendingAllowance null case was removed, because empty now refuses.
4 done: `Ui.Confirm`/`ConfirmIf` gained refusal overloads that are asked before arming, on either press. `AmountField.Figure` throws instead of falling back. The `?? 0m`/`?? current` fallbacks and the lane clamp are gone.
5 done: cap, plan, price, allocation, envelope and perception sentences and saved notes use Amount.Exact/ExactMoney. Not changed: WidenLimitsArmed names the widened limit, not its figure, as before.
6 done: a page Update/Refresh/card-refresh check plus a source guard (no `.Box.Text =` in App, no outside setter on Value).
7 done: a wheel over all 26 boxes, unfocused and focused, leaves Value and Text alone and Handled false. The boxes have no template in the test (no window), so the real TextBox template's ScrollViewer was not exercised.
Tests box: NOT RUN — the machine does not answer. Screenshot NOT RUN — the observed run holds this Mac. Codex re-review HELD for the observed run (prompt file written; no codex started).
NOT done or verified: the app was not launched, so the reading line's wrapping in its 150-wide column was not seen; no full local suite (CI ran it).

## Fix pass 1
Code tip `9502491e` (on 46bfc570; main had not moved, base 988c9487). Gate: build Release --no-incremental "0 Warning(s) 0 Error(s)"; touched classes (NumberEntryCulture, TwoPressGrant, MissionCostSurfaces, PaperFriction, LossBudgetSurfaces, PaperEnvelope, BudgetReservation, BillingClasses, OrgLanes, PerceptionCard) 3× "Passed! Failed: 0, Passed: 165"; CI 39835206346 success 12/12; names.sh "removed: 0, added: 14" (2965 → 2979); tests box run ta-run-20261022-205316-9502491e GREEN (Unit 2143+20, Fault 498+6, Integration 733+91 passed, 0 failed; his files unchanged).
1 done (5ceabf3b): `SafetyPage.DisarmedByEdits` disarms Save limits (nine boxes and the allowlist), the AI price and review-every on any edit. `Labels.WidenLimitsArmed` names each widened limit "to <exact figure>" (`RiskPolicy.FigureOf`). The limits note names each changed limit "is now <figure>" (`RiskPolicy.Changes`). TwoPressGrantTests' widen-sentence asserts were rewritten to carry the figure, and the "several are counted" case now names both figures.
RED 1 at 46bfc570: "2000" typed under an armed Save limits, `Assert.False() Failure Expected: False Actual: True`. MUTANT (the boxes' ValueChanged disarm in DisarmedByEdits removed): the same assert fails, "Failed: 1, Passed: 44"; restored, green.
2 done (790f999b): `Amount.MostDays/MostHours/MostMinutes` (a hundred years) on envelope days, strike window, closure, valuation exit and review-every. RED: "3000000" threw `ArgumentOutOfRangeException … un-representable DateTime` from ReadEnvelope ← AmountField.Read. Now it is refused in the reading line.
3 done (63fbdb9a): TryRead refuses more than 28 places, more than 29 digits, or a printed-back figure that differs (`Labels.AmountTooPrecise`). RED: "0.00000000000000000000000000001" read as "0". A 30-digit integer is refused (too large).
4 done (9502491e): a live reading line under the paper fee and slippage, from the same `TryPercent` the save uses. "0,5" reads as 0.5%, "1.000" is refused and the save writes nothing, and empty says "no override" in words (`Labels.PaperFeeEmpty/PaperSlippageEmpty`). RED: "the Settings page has no _paperFeeReading".
Deviation: a hundred years is a new stated maximum on five duration boxes. It is chosen only so every figure stays a date.
NOT done: the Claude re-review is HELD for seat B, with its prompt at fleet/tmp/B-scratch/u-number-entry-culture-fix1/rereview-prompt.md and no codex started. The app was not launched and no screenshot was taken (the observed run holds this Mac), so the new reading lines' wrapping was not seen.
