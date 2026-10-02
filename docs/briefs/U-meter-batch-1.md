# U-meter-batch-1 — a harness turn with no key held sends nothing and is charged nothing; two comments made true after the 2026-10-02 price reading
**Protects:** spend accounting — the day's cap counts what the owner was billed for, a reservation is a ceiling, and "unknown is never zero"
(`docs/COUNCIL.md` rule 3; `docs/EDGE-FACTORY.md` § 6.7). A turn the app itself never sent is not unknown: charging it the reservation spends the cap on
nothing and can refuse real work. **Light leg, seat P, after `U-org-ledger`** (the orchestrator, 2026-10-02). No schema change; not a money-path file.
**Item 1 — today (SOURCE at `5427746`, read by seat P; NOT runtime-verified):** with no key held, `ApiConversation.RunAsync` appends `Labels.HarnessKeyNotHeld`
and ends the turn `Outcome = EndedNotStarted` before any request is built (`src/TradeAgent.AgentRuntime/ApiConversation.cs:277-288`) — but it carries no
marker of that, unlike the withheld-key path just above it (`:263-275`, `KeyWithheld`, `U-key-host-pin`). `TurnMeter.Charge` charges zero only for
`Limit.BeforeAnyWork` or `KeyWithheld` with no usage (`TurnMeter.cs:685-688`, the rule written out at `:660-684`); everything else goes to
`AiAttemptStore.End`, which keeps the reservation as the cost when no usage is reported (`Core/Db/AiAttemptStore.cs:333-338`). So the admitted no-key turn
is charged its whole reservation (found by a seat-A builder on 2026-10-02; reproduce it red first — the brief is not the proof).
**Observable result:** a harness turn that ends because no key is held writes cost 0 with the app's own sentence on the row, exactly as the withheld-key turn
does; every other no-usage turn keeps its reservation — a turn that showed work, a stream nothing could read, a vendor refusal after work.
**Item 2 — stale comments after `U-price-rows` (2026-10-02 reading):** `RuntimeManifest.cs:635-637` says `gpt-5.6-sol` is the default "because it is the
mid-priced current model … (4.00 in / 20.00 out … against astra's 10.00/50.00)", and `:678-681` says `gpt-5.6-luna` is "the cheapest current model on the
catalogue this build ships (0.20 in / 1.20 out …)"; since that reading, `gpt-6.1-sol` and `gpt-6-sol` (2.00 / 10.00) and `gpt-6-luna` (0.10 / 0.50) are on the
list (`ListPrices.cs`), and the `gpt-5.6-sol` figure is promotional until at least 2026-11-21 (`BUILD-STATUS.md`, the U-price-rows record). Rewrite both
comments to say what is true now, dated, from `ListPrices.cs` — and change NO `DefaultModel`: which model a seat defaults to is the owner's decision, not this
unit's; say in the comment that the default predates the GPT-6 rows.
Read first: `CLAUDE.md`; `docs/HOW-WE-BUILD.md`; `ApiConversation.cs:236-300`; `TurnMeter.cs:640-720`; `AiAttemptStore.cs:305-350`; `Labels.HarnessKeyNotHeld`
(`Core/Errors.cs:287`); the precedents `HarnessKeyOriginTests.cs:158` (`An_origin_mismatch_refuses_before_any_request_clears_the_key_and_charges_nothing`)
and `VendorLimitTests.cs:298`; the guards that must stay green, `VendorLimitTests.cs:325` (`…_keeps_its_reservation_as_its_cost`) and `ApiWorkerTests.cs:171`
(`A_response_that_reports_no_usage_leaves_the_turn_unresolved_rather_than_zero`); `RuntimeManifest.cs:600-700`; `ListPrices.cs`.
Must NOT: make any other no-usage path zero (unknown is never zero); infer "nothing sent" from an exit code, an outcome string or a missing stream — the marker
is set on the ONE path that returns before a request is built, as `KeyWithheld` is; refuse admission instead (a separate design, not this unit); change a
default model, a price or a reservation figure.
Items, one commit each, one-sentence messages:
1. The no-key turn carries the app's own record that nothing was sent and the meter charges it zero with that sentence on the row (`context.refused` or its
   equivalent, as the withheld-key turn has); `CONTRACTS.md` (its cost section) and `docs/USER-GUIDE.md` say so in one sentence each if they describe the rule.
2. The two comments, true and dated; no behaviour change.
Red-first test: `A_harness_turn_with_no_key_held_sends_nothing_and_is_charged_nothing` — the attempt ends with cost 0 (not the reservation), the app's sentence
on the row, no request made (assert the handler saw none) — written FIRST and quoted red at base. Mutant to watch red and quote: drop the new marker from
`Charge`'s zero rule ⇒ the test red with the reservation as the cost.
Gate and report per `docs/HOW-WE-BUILD.md` and `docs/FLEET.md` "The builder pass": rebase on `main` first; `--no-incremental` Release 0 warnings; Unit and Fault
0 failed; touched classes 3×; names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push to `main`, no merge; touch
nothing in `docs/briefs/` but this file.
