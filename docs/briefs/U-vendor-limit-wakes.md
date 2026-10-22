# U-vendor-limit-wakes — a turn the vendor refused for its usage limit before any work gives its wakes back instead of losing them, and the limit is also recognised by the vendor's error code, never by a widened sentence

**Arrow closed:** survey S-attempt4 (`fleet/tmp/M-attempt4/blockers.md`, D2 and D3; main `25bae9bc`). The owner's plan is ChatGPT **Plus** (his answer,
2026-10-21): the smallest paid tier, shared with his own Codex use and the fleet's reviews — an unattended loop on it (the switch-on, M-org1) will meet the
vendor's limit as a matter of course, and today each meeting loses the wakes it took. **A protection — spend (BUDGET-001: a refusal before any work is
charged nothing), the owner's words never lost, refuse-never-guess: red-first and ONE watched mutant.** Seat B; **opus/high** (the spend and wake path).
**No rung.** Depends (landed): `U-vendor-limit`, `U-vendor-limit-quote`, O3 `U-org-wakes`, `U-thinking-seats-a` (the gate's give-back, the model to follow).
Not a GO condition for attempt 4 (its runbook pauses the AI at a met limit), but land it before it if it is ready: the run is the first chance to see it.

**Today (main `25bae9bc`):**
- `MissionLoop.Settle` (`AgentRuntime/MissionLoop.cs:3709-3735`) gives a launch's wakes back only when the activation gate refused it (`ended.GateRefused` →
  `events.Release`, the owner's words `Blocked`); any other failed turn settles EVERY wake `Failed` and retries only the owner's words, once. A turn the vendor
  refused for its limit exits nonzero, so its brief, report and self wakes are lost though no work was done — the loop's own record of what was owed.
- The limit is recognised only by its sentence, `Pattern = "You['’]ve hit your usage limit"` (`AgentRuntime/RuntimeManifest.cs:957`), read where the
  stream reports an error (`AgentSession.cs:1158-1171`, `state.Limit … BeforeAnyWork = !state.Worked`). The U+2019 form on `codex exec --json`'s stdout is
  NOT VERIFIED (`:942-944`), and nothing reads the vendor's error CODE (`usage_limit_exceeded` appears in codex's session logs — the logger greps it,
  `fleet/tmp/M-attempt4/runbook.md` § 3). An unrecognised limit is charged as an ordinary failed turn and retried at the loop's backoff, not the vendor's time.

**Items, one commit each:**
1. **The wakes come back.** In `Settle`, a turn whose `ended.Limit is { BeforeAnyWork: true }` releases its wakes as a gate refusal does, due again when the
   vendor's hold lifts (the hold the loop already keeps, `MissionLoop.cs:2428`); the owner's words among them stay owed and shown `Blocked` with the limit's
   own sentence — never settled `Failed`, never retried as one. A limit met AFTER work keeps today's settlement (the work happened). RED first: a stub turn
   refused before any work → its brief/report/self wakes read `Failed` (quote); then due again after the hold, the owner's words `Blocked`.
2. **The code is data too.** The manifest's `UsageLimitPlan` gains the vendor's error code as data beside the sentence (vendor commands are data,
   `runtimes.json`), read from the error event's own code field wherever `codex exec --json` carries it — find its shape in a recorded stream (a codex session
   log under a THROWAWAY home with the mock provider, `fleet/tmp/A-org/seat-home/mock/`; never the owner's `~/.codex`) and quote it; if stdout does not carry
   the code at all, say so, keep the sentence, and the item becomes the fixture that proves it. Never a bare "usage limit" match and no `IgnoreCase`
   (`RuntimeManifest.cs:950-955` says why). RED first: a recorded limit event with the code and a sentence the pattern does not match → charged and retried at
   backoff today (quote); recognised and held after.
**The watched mutant:** item 1's branch removed ⇒ its test red.

**Gate:** Release `--no-incremental` 0 warnings; touched classes 3× through `fleet/bin/suite.sh`; names vs main 0 removed; one branch CI (three platforms);
the Codex review — HELD while an observed run is live (it spends the same Plus plan). **Read first:** `MissionLoop.cs:2410-2470, 3680-3760`;
`AgentSession.cs:1140-1200`; `RuntimeManifest.cs:930-1000`; `TurnMeter.cs:760-790`; `VendorLimitTests.cs`, `VendorLimitQuoteTests.cs`,
`VendorLimitReadTests.cs`; ORCHESTRATION § 2.5 (refusal kinds, give-back).
**NOT in scope:** a limit met after work began; any vendor but codex; the VendorLimit/TurnMeter test-host sightings (`U-turn-tail-drain`'s).

## Report
Code tip `95cd9757` (on base `f7b2ab02`, no rebase needed). Build: `dotnet build -c Release --no-incremental` → `0 Warning(s)` `0 Error(s)`.
Class runs, VendorLimitTests 3× through suite.sh: `Passed! - Failed: 0, Passed: 9, Skipped: 0, Total: 9` ×3. ThinkingSeats + OwnerDisposition + VendorLimit* once: `Passed: 51`.
CI 39686686618 (attempt 1): every job green except test (ubuntu-latest) — PromptStdinTests ×7 `Actual: 154` (ETXTBSY, the launcher-race sighting U-launcher-race chases; my diff does not touch PromptStdin, which was green on macOS and windows ×5). Rerun of the failed job (attempt 2): `success`, all 12 jobs green.
names.sh main → `removed: 0`, `added: 3`, sets base 2968 / tip 2971.
Item 1 DONE (209880e1): `Settle` gives back every wake of a turn whose `Limit.BeforeAnyWork` (one helper `UnworkedRefusal`, beside the gate's refusal); owner's words `Blocked` with the vendor's sentence, never `Failed`/retried; due again once the existing hold lifts. Deviation: words TYPED into such a turn are also owed again (`OweTyped`), as for a gate refusal — the owner's words never lost.
RED before: `A_turn_the_vendor_refused_before_any_work_gives_its_wakes_back_until_the_hold_lifts` → `brief:operations:f1fc6e… was spent on a turn the vendor refused: failed`.
Mutant (the limit branch of `UnworkedRefusal` deleted): same test red, `brief:operations:8c741b… was spent on a turn the vendor refused: failed`; restored. Guard: a limit met after work still settles `failed` + one retry (green).
Item 2 DONE as the fixture (95cd9757) — deviation per the brief's own clause: codex-cli 0.160.1's `codex exec --json` stdout carries NO error code; `usage_limit_exceeded` is only in the session log (`codex_error_info` on `task_complete`). Manifest keeps the sentence; no code field added; `VendorLimitTests.Recorded0160` + test (recognised, charged 0, no code on any line). Green on the base (a guard, not a red).
How recorded: codex 0.160.1, 2026-10-21T22:22:18Z, throwaway CODEX_HOME + HOME in the B scratch, fake key, `env -i`, a local 127.0.0.1 mock (a 429 `usage_limit_reached` variant of the A-org mock); never the owner's ~/.codex.
Codex review: `fleet/tmp/B-scratch/u-vendor-limit-wakes/U-vendor-limit-wakes-review.md` (relaunched by seat B; not acted on).
tests box: NOT RUN — a run was in progress (another unit's) when `ready` was asked once.
NOT verified: the vendor's own server's 429 body (the mock's is codex's parsed shape, not a recording of OpenAI's); U+2019 on stdout is now seen under that mock, not under the owner's real limited plan. A limit met after work: unchanged, out of scope.
