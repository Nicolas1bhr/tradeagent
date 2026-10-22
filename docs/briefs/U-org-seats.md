# U-org-seats — the meter, the conversation and the harness bound read one seat — runtime, model, allowance — resolved once per launch, so a reservation smaller than the shipped bound is admitted only where the turn is bounded to it, and every attempt records the allowance it was reserved at and whether that bound is enforced
**Arrow closed:** `docs/ORCHESTRATION.md` § 2.1 ("Seat … runtime profile × model class × effort × playbook version × allowance × cell class"; "a seat moves only by
an app rule or the owner"), § 2.4 ("incurred cost is bounded only where a total-turn bound … is demonstrated for that runtime; elsewhere the reservation is the
bound, and an overrun is recorded and charged, never hidden"), § 2.7 ("Usage: what the provider reports; otherwise the reservation stands"), § 5 (the harness
row "bounded per request; the turn bound shown, not assumed"); ORGANISATION § 10 (:302-304 "A seat is a runtime, a model class, an effort level, a per-turn
allowance and a price basis"); R15 § 3c ("A smaller allowance is only honest where it is enforced"); R18 #1 (the cut from O4); § 15 `U-org-seats` (:422).
**Protections — BUDGET-001 (reserve before launch; a reservation below the shipped bound only on an enforced bound; unknown never zero), measurement vs claim
(allowance and bound class app-composed on the row), AUTH-001 (the seat writer in-process; no op, verb or door reaches it), refuse never guess (a malformed
seat refuses the launch in words, wakes kept): red-first and ONE watched mutant.** Seat B; **opus/high** (the money path, a rung). **A rung (the next free at
dispatch; main's is 42):** `ai_attempt.allow_in`, `allow_out`, `turn_bound`. No canon change.
**Depends (landed):** O4 `U-org-envelopes` (rung 38), `U-admission-fail-closed`, `U-thinking-seats-c` (the broker pin). Lands BEFORE `U-billing-classes`
(serial: same files, the class read off the same verdict). Survey: `fleet/tmp/B-scratch/s-org-seats-billing/survey.md`.
**Today:** `org_position`'s `seat_runtime/seat_model/seat_allow_in/seat_allow_out` exist (rung 28, `Database.cs:1808-1819`) and nothing reads or writes them
(`OrgStore.cs:289-300`; seed NULL `:2792-2797`; O6-c hires NULL). The model is resolved twice — the meter prices `AppHost.RequestedModelFor` (`AppHost.cs:457-467`),
the conversation opens on `Settings.ModelForRole` (`:365`). ONE app-wide allowance goes separately to the meter (`:1010-1011` → `TurnMeter.cs:1194-1210`) and
to the harness (`:223-225` → `ApiAgentRuntime.cs:57,193` → `ApiConversation.cs:396`); the CLI has none. The harness checks only before a request
(`ApiConversation.cs:473-494`) and asks every request for the whole output allowance (`:554`), so its last request can pass the bound by a context and an
allowance. The Safety page lowers the allowance in one press (`DashboardView.cs:2508-2522`), which on a CLI seat lowers the reservation with nothing enforcing it.
**Observable result:** a position seated on the harness at model M with allowance 300 k / 8 k reserves 300 k × max(input, cache-write) + 8 k × output at M's
row; its harness conversation sends M, asks each request for at most the output still unspent, and does not send a request whose body could take input past
300 k — the turn ends `CONTEXT_BUDGET_EXCEEDED` with its work kept; its `ai_attempt` row reads `allow_in 300000, allow_out 8000, turn_bound enforced`. The same
seat on the codex CLI reserves at max(seat, 1.2 M) / max(seat, 20 k) and its row reads `turn_bound reservation`. The model on every launch's argv or request
body equals the row's `requested_model`, broker pin included. A seat column that does not parse refuses the launch in words naming the position and the
column — no row, no reservation, wakes kept. The owner seats a position in-process (`OrgStore.SetSeat`, one `org_event seated`, decider `owner`); no pipe op,
door verb or `trade` verb can.
Read first: the survey §§ 1–3; ORCHESTRATION §§ 2.1, 2.4, 2.7, 5; `TurnMeter.cs:873-990` (Begin), `:1036-1110`, `:1185-1225`; `AppHost.cs:200-235, 280-370,
425-467, 995-1023`; `ApiConversation.cs:40-60, 385-500, 545-560`; `ApiAgentRuntime.cs:40-110, 175-200`; `CliAgentRuntime.cs:960-990`; `OrgStore.cs:280-340`;
`Database.cs` rung 38/39 (the `ai_attempt` column precedent); `RuntimeManifest.cs:1640-1810` (`Reserve`, `Bound`).
Items, one commit each, one-sentence messages:
1. **The spec.** `SeatSpec(Runtime, Model, Allowance, Bound)` in Core and ONE resolver (`AppHost.SeatFor(role)`): the position's columns when set (all four, or
   refused as malformed — never half a seat), else today's sources (the pair: `RuntimeForRole`, `RequestedModelFor` with the broker pin; other positions: the app
   default) and the app-wide allowance; `Bound = Enforced` only for a seat whose runtime is the app's harness, else `Reservation`. Settles: (c), (e).
2. **The meter reads it.** `TurnMeter` takes `Func<string?, SeatSpec>` in place of `_allowance`/`_roleModel`/`_roleRuntime`; `Reservation(role)` prices
   `Enforced` at the seat's allowance and `Reservation` at the per-component max with `TurnAllowance.Default`; `Begin` records `allow_in/allow_out/turn_bound`
   (the rung, UPDATE of the three refused by trigger, the `Versions` note). The conversation opens on the spec's model (`AppHost.cs:365`). Settles: (a), (b), (g).
3. **The harness bounds the turn.** `ApiAgentRuntime`/`ApiConversation` take the per-role spec: each request's `MaxOutputParam` = allowance.out − used.out;
   a request is not sent when used.in + its body's UTF-8 byte count > allowance.in (stated INFERENCE in the code comment: a byte-level BPE token covers ≥ 1 byte;
   the vendor's tokenizer NOT VERIFIED); the existing `Exceeded` words kept. Settles: (d).
4. **The writer and the words.** `OrgStore.SetSeat(position, runtime, model, in, out)` in-process (validated: a runtime in the catalogue, positive tokens; an
   `org_event seated`; no `Ops` entry); `Labels.TurnAllowanceHint` says a smaller allowance lowers what is set aside only where the turn is held to it (the
   app's own harness), never on the vendor's program. Settles: (f), (h).
Red-first (each red at base quoted): (a) `A_seat_allowance_prices_the_reservation_and_bounds_the_harness_turn`; (b) `A_cli_seat_never_reserves_below_the_shipped_bound`;
(c) `The_model_launched_is_the_model_reserved_for_on_every_seat_kind` (CLI, broker pin, harness); (d) `The_harness_never_sends_a_request_that_could_pass_the_seat_allowance`
(mock transport: remaining output asked; an oversize body withheld); (e) `A_malformed_seat_refuses_the_launch_in_words_and_keeps_its_wakes`;
(f) GUARD `No_pipe_op_door_verb_or_trade_verb_writes_a_seat`; (g) `Each_attempt_records_its_allowance_and_whether_its_bound_is_enforced` (+ the rung's
crash-between-statements reopen test); (h) `Lowering_the_allowance_lowers_no_cli_reservation`.
**The watched mutant:** the harness conversation reads the app-wide allowance instead of the seat's (item 3 reverted to `AppHost.cs:223-225`'s Func) ⇒ (a) red.
Quote it, restore, green.
**Evidence beyond CI:** a puppet life (`PuppetLivesTests` pattern) with one harness-seated and one CLI-seated position: the two rows' `reserved_cost`,
`allow_in/out`, `turn_bound` printed beside the day's reading. No model.
**NOT VERIFIED until a real run:** the byte bound against the vendor's reported input tokens (the first metered harness turn compares them); a real harness turn
at all (no API key is held; M-org1 or later).
**NOT in scope:** billing classes (`U-billing-classes`, next); effort, playbook version and cell class on the spec; a versioned seat table or a model-CLASS
mapping (§ 2.16, `U-org-templates`); the Org page's seat control (O7 `U-org-surface`); per-seat lanes or quota pools; a hire op naming a seat.
Conflicts: `U-billing-classes` (serial after this); O6 (`OrgStore*.cs`, the rung order — the board orders rungs; O6-c's `Unfundable` reads `TurnMeter.Reservation`,
which this makes seat-true); `U-org-packets-b` (`TurnMeter.cs`, the rung); seat A's 2.7 files (`ApiConversation.cs`, `ApiAgentRuntime.cs`, `CliAgentRuntime.cs`) —
the orchestrator tells seat A. Gate and report per `fleet/BUILDER-PROMPT-v2.md` (the rung's crash-between-statements reopen test; names vs `main` 0 removed;
three platforms; Codex review; tests box line). `## Report` ≤ 20 lines appended here.

## Report
Tip `94ae11f4` (code; this report commit on top), base `fbd1afa4`. Gate: 1 `Build succeeded. 0 Warning(s) 0 Error(s)` (Release, --no-incremental). 2 the 9 touched/added classes 3× through the lock: `Passed! - Failed: 0, Passed: 103` ×3. 3 CI 39716329234 on 94ae11f: 12/12 success (linux-host 20 min); the first run 39714259142 was red on ubuntu, macOS and win-4 with ONE test, `OrgLedgerTests.No_pipe_op_writes_an_org_table` (OrgStore's member list lacked SetSeat) — mine, fixed by revising that guard OUT LOUD (SetSeat and OrgStore.Seats.cs named as the owner's writer). 4 `names.sh`: `removed: 0`, `added: 9`, [Fact]/[Theory] 2861 → 2870.
Item 1 done: `SeatSpec` + `SeatColumns` (Core), `SeatResolver` (all four columns or refused in words naming position and column; legacy pair keep the Safety page, Q5), `AppHost.SeatFor`; a malformed or unservable seat (a CLI other than the prepared one) is a HostOrLaunch refusal at the activation seam, wakes kept.
Items 2+3 done, ONE commit (deviation: the meter's admitted seat and the harness read it through one interface, `ISeatedConversation`, set by `TurnMeter.Attach`): `seat:` replaces `allowance/roleModel/roleRuntime`; Enforced priced at the seat, Reservation at max(seat, 1.2 M/20 k); Begin resolves the seat ONCE (before the seat check), records allow_in/allow_out/turn_bound; the CLI conversation opens on the admitted seat's model (`ConversationModelFor`), a broker seat still handed the choice it verifies against its pin; the harness asks remaining output and withholds a body whose UTF-8 bytes + reported input could pass the allowance (INFERENCE in the code, NOT VERIFIED against the vendor's own count). Four harness tests rescaled to millions (the byte bound would stop them before any request) and HarnessRoleTests' chair figure moved to the floor — declared, intent kept.
Item 4 done: `OrgStore.SetSeat` in-process (one `org_event seated`, decider `owner`; legacy pair, unknown runtime, bad model, non-positive figures refused); hint + USER-GUIDE words. Extra (in scope of (c)): the chair's supervisor reads its own seat's model, not SelectedModelId — NOT covered by a test (no unit host prepares a CLI that names a model).
RED: all nine new tests red together with the unit's behaviour reverted (floor, admitted seat, remaining output, byte check, trigger, conversation model, seat reader): `Failed: 9, Passed: 0`. MUTANT (harness reads the app-wide allowance, not its admitted seat) ⇒ (a) red: `Expected: [8000, 5000, 2000] Actual: [20000, 17000, 14000, 11000, 8000, ···]`; restored, green.
Rung 45 (allotted; renumber = `seatsRung` in Database.cs, `Versions`, the test's `Rung`): reopen with the trigger dropped and with only the stamp missing — schema back to 45, trigger present, row intact, UPDATE still refused: green.
Scan exclusions (exact phrases, all AI-usage "token" words): `InputTokens|OutputTokens|max_completion_tokens|per million tokens|byte-level BPE token|positive token figures|bounds the tokens it can be billed as|tokenizer is NOT VERIFIED|at least one token|input and output tokens|at a token a|input tokens of the|INPUT TOKENS, reached EXACTLY|a token a byte at most|a token figure that is not|= Tokens\(row.Id|static long Tokens\(|whole number of tokens`.
Review: `fleet/tmp/B-scratch/u-org-seats/U-org-seats-review.md` (ran on tip 57f0c225; NOT acted on).
Tests box: NOT RUN — another fleet run was in progress (`ready` exit 4).
NOT done/verified: the puppet-life evidence (a position beyond the pair is stepped over without a proven cell; a harness seat needs a held key) — the rows are shown by (a)(b)(g) at the meter; the byte bound vs the vendor's reported usage; any real harness turn; ORCHESTRATION/ORGANISATION doc edits (Q11, the landing's).

## Fix pass 1
Tip `2a206e67` (code; this report on top), on `fbd1afa4` (main unmoved). Gate: `Build succeeded. 0 Warning(s) 0 Error(s)` (Release, --no-incremental); OrgSeatsTests + Harness*/ApiWorker/TurnMeter/ThinkingSeats/Canon 3× through the lock: `Passed! - Failed: 0, Passed: 115` ×3; HarnessTradeArgsTests `Passed: 3`; `names.sh`: `removed: 0`, `added: 12` (2861 → 2873).
Item 1 (P1, `8bb6a4c3`): a response with no usage → no further request; the turn ends `usage-unreported` in words naming that request, its usage NULL (never zero, never the other responses' sum), the row charged its reservation with `context.usage_unreported` naming the request; a final response without usage completes with the same record. RED at base: `Expected: [8000] Actual: [8000, 8000]` (a second request asking the whole output again) and `Expected: null Actual: TurnUsage { InputTokens = 1000, … OutputTokens = 100 … }`. MUTANT (`else unmeasured ??= requests;` removed) ⇒ the same two reds quoted again; restored, green.
Item 2 (P2, `2a206e67`): a first request withheld for size sets `AgentTurnEnded.FirstRequestWithheld` (the app's own record, like `KeyNotHeld`); the meter charges zero, the sentence on the row's `refused`, no canon recorded; wakes are consumed as with `KeyNotHeld` (a too-large Situation does not loop). RED at base: `Expected: 0 Actual: 0.0321` (the full reservation 100 / 8 k at 1 / 4 per million), zero HTTP requests.
CI 39719368509 on 2a206e67: 12/12 success (linux-host included). Scan exclusions: the builder's exact phrases plus `_cts.Token` (`InputTokens|OutputTokens|max_completion_tokens|per million tokens|byte-level BPE token|positive token figures|bounds the tokens it can be billed as|tokenizer is NOT VERIFIED|at least one token|input and output tokens|at a token a|input tokens of the|INPUT TOKENS, reached EXACTLY|a token a byte at most|a token figure that is not|= Tokens\(row.Id|static long Tokens\(|whole number of tokens|_cts.Token`).
Tests box: run 20261022-055423-2a206e67 GREEN — Unit 2035 passed / 0 failed (19 not run), Integration 728 / 0 (41 not run), Fault 498 / 0, timing 117 / 0; his files unchanged.
Re-review: `fleet/tmp/B-scratch/u-org-seats-fix/rereview.md` — "No defects found. `8bb6a4c3` closes P1; `2a206e67` closes P2."
NOT verified: a real provider omitting usage (mock only); the byte bound against vendor counts (as before).

## Rebase onto U-org-packets-a
On `598278f7` (packets-a's rebased tip, on main `7550906b` = rung 43). Conflicts, both units kept: `Database.cs` rung block — U-cell-linux-c's 43 then seats' rung after it; `Versioning.cs` — the 43 note and seats' note, one `DatabaseSchemaVersion`; `TurnMeter.cs` `_open` (538) and its write (1009) — the tuple carries packets-a's `Packet` AND seats' `Spec`; fix pass 1: `MissionLoop.cs` `AgentTurnEnded` (385-399) and `TurnMeter.cs` `TurnContext` (272-295, 819-822) carry `Session`/`SessionWhy`/`Packet` AND `UsageUnreported`. Each conflicted commit built: `0 Warning(s) 0 Error(s)`.
Renumber `55abff46`: rung 45 → 44 (`seatsRung`, `Versions`, `AiAttemptStore` comment, `OrgSeatsTests.Rung`). `45de7d0f`: `CellProofTests.The_proof_and_assurance_tables_refuse_every_edit` asserted a fresh home's stamp `== "43"`, red once 44 exists — now `InRange(stamp, 43, Versions.DatabaseSchemaVersion)`, the check that rung 43 is reached kept.
Gate: Release --no-incremental `0 Warning(s) 0 Error(s)`; OrgSeats/HarnessBudget/CouncilLoop(+Session)/PuppetLives/TurnMeter/CellProof/ApiWorker/Canon/HarnessKeyOrigin/HarnessLoop/HarnessRole/OrgLedger/ThinkingSeats 3× through the lock: `Passed! - Failed: 0, Passed: 200` ×3; whole unit project once: `Passed! - Failed: 0, Passed: 2098, Skipped: 6`; `names.sh origin/u-org-packets-a u-org-seats`: `removed: 0`, `added: 12`. No contiguity/ladder test class exists by that name; the rung's own reopen test is in OrgSeatsTests (green).
Scan exclusions: unchanged from fix pass 1 (no new hit). CI NOT dispatched here (the landing's).
