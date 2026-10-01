# U-vendor-limit — when the AI's own plan runs out, the app says so in the vendor's words, waits until the stated time, and charges a refused turn nothing
**Boundary and arrow:** the spending boundary and "useful waiting" (`docs/PRINCIPLES.md`: "Budget exhaustion stops paid work"; "Keep actual charges,
estimates and subscription/list-price equivalents distinct … unknown is never zero"; `manager-prompt.md` § 6: "budget exhaustion behaviour"). **Observable
result:** in the running app, a turn the vendor refuses for usage puts the card on "<vendor>'s usage limit is reached — the AI waits until HH:MM" in the
vendor's own words, launches no turn before that time, and charges the refused turn 0 when the vendor refused it before any work; a turn cut by the limit
mid-work keeps its reservation as cost (unknown is never zero).
**The gap, RUN 2026-10-01 (`docs/briefs/U-observed-loop.md`, attempt 2, app at `f8a7500`, codex-cli 0.153.4, gpt-5.6-luna):** attempt `…1808061` (research)
ran 2.9 s, 0 command items, exit 1; the codex session log said "You've hit your usage limit. Upgrade to Pro … or try again at 10:30 PM."
(`codex_error_info: usage_limit_exceeded`); the app charged it the full 0.324 reservation ("the turn ended without reporting what it used"), the card said
"OpenAI Codex CLI did not finish: Reading additional input from stdin..." (stderr's first line, not the reason), and the loop scheduled its next look for
18:38Z, inside the limit. The turn before it (`…1802091`, 20 command items, exit 1, no usage) was cut by the same limit mid-work.
**Where (SOURCE):** `AgentSession.cs:587` ("did not finish: {detail}") and `:674-679` (an `error` event becomes a System turn); `MissionLoop.cs:1644`
(`Backoff(errors)`) and `:2115-2121`; the reservation's fate on an unreported turn (`TurnMeter.cs`, `AiAttemptStore`); the codex manifest
(`RuntimeManifest.cs:368-400`; vendor behaviour is DATA — `CLAUDE.md` "Vendor commands are data, not code", `runtimes.json`).
Read first: `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, the files above, `docs/CONTRACTS.md` on the meter and the mission.
**First, capture the vendor's real output** (the owner's codex plan is limited until 20:30Z on 2026-10-01): run ONCE, before 20:30Z, `codex exec --json
--skip-git-repo-check "reply ok"` in a scratch folder and keep its stdout and stderr verbatim as a test fixture (strip nothing but a session id). If the
limit has already reset, do NOT spend the owner's allowance to provoke it: build from the session log quoted above and say NOT VERIFIED for the stream shape.
Items, one commit each:
(a) **Recognise it.** The runtime's turn end carries a typed vendor-limit reason and, where the vendor states one, the retry instant (the signature as
manifest data for codex; OpenCode untouched unless its signature is known); the System line shown is the vendor's message, not stderr's first line.
(b) **Wait for it.** A vendor-limit end holds every launch until the retry instant (due wakes are kept, not consumed into failing turns; no stated instant →
the existing backoff); the card and the activity log say it in the owner's words; "Pause the AI" and the owner's own chat are untouched.
(c) **Charge it honestly.** A turn the vendor refused before any model item and with no usage is recorded with cost 0 and the vendor's reason; any turn with
work before the refusal keeps the reservation as its cost, as today. State the evidence each branch reads.
(d) **Tests, RED on the base,** from the captured fixture: the reason and instant parsed; no launch before the instant; the two cost branches. Watch ONE
mutant (the hold removed) turn the no-launch test red and quote it.
Not the money path; the paid-turn trigger and the cost record — red-first is the proof. No schema rung unless the reason needs a column (then say why).
Gate as `docs/HOW-WE-BUILD.md` (`--no-incremental` Release 0 warnings; the three suites 0 failed on this Mac, counts pasted; touched classes 3×; names 0
removed). Report ≤ 20 lines appended here. No `Co-Authored-By`; no push to `main`; touch nothing in `docs/briefs/` but this file.
## Report
**Code tip `a39ca4f`**, 4 commits on `89328a0` (rebased over U-resume-agent: one conflict, `MissionSentence`, main's `notStarted` kept + the hold's arm; each commit builds alone, 0 warnings). **Capture,
once, 2026-10-01T18:20:07Z** (codex-cli 0.153.4, scratch folder, stdin closed): exit 1 in 4 s; stdout `thread.started`, `turn.started`, `error` "You've hit your usage limit. … try again at 10:30 PM.",
`turn.failed` (same); stderr "Reading additional input from stdin...". Verbatim in `VendorLimitTests`, thread id zeroed. Dated form, "try again later.": `strings` of that binary, NOT VERIFIED in a stream.
**Gate, this Mac, Release, suites alone:** build `--no-incremental` → 19 projects, `0 Warning(s)`, `0 Error(s)`; Unit `Passed! - Failed: 0, Passed: 1212, Skipped: 0, Total: 1212, Duration: 28 s`; Fault
`Passed! - Failed: 0, Passed: 399, Skipped: 0, Total: 399, Duration: 1 m 28 s`; Integration `Passed! - Failed: 0, Passed: 699, Skipped: 1, Total: 700, Duration: 11 m 6 s`; touched classes 3× → 72/72 each
(`VendorLimit*`, `TurnMeterTests.cs`'s five, the probe's users `AiAttemptLedgerTests`/`BudgetReservationTests`). Names vs `main` (git objects): 1920 → 1934, 0 removed.
- **(a) `97917fd`:** `RuntimeManifest.UsageLimit` is data (codex: pattern, retry regex, five exact formats; OpenCode none). The session ends the turn with `AgentTurnEnded.Limit` (vendor, sentence, retry instant,
  `BeforeAnyWork` fixed at the refusal) and the vendor's sentence as the last System line — no "did not finish: <stderr>", no raw-stream dump.
- **(b) `a96a919`:** holds per runtime (`IMissionHost.RuntimeFor`, AppHost → `RuntimeForRole`; all roles where a host can't say) until the END of the named minute, else now + the existing `Backoff`; held roles
  are stepped over (wakes stay due); re-looks ≤ `MaxDelay` (asserted). Card "…: OpenAI Codex CLI's usage limit is reached — the AI waits until 22:31"; log = that + the vendor's sentence, once; Pause, owner's chat untouched.
- **(c) `b31dc45`:** `TurnMeter.Charge` — usage → as before; no usage AND `Limit.BeforeAnyWork` (no item of any kind, text, tool or usage when the refusal arrived) → 0, `unpriced_reason` null, `context.refused` +
  `ended: VENDOR_USAGE_LIMIT`; every other no-usage turn → the reservation via `AiAttemptStore.End`, unchanged. No schema rung (`context` carries it); `CONTRACTS.md` says so.
- **Time zone:** codex prints the reset in the machine's zone ("10:30 PM" at 18:20Z on this CEST Mac = 20:30Z); the app reads it in `TimeZoneInfo.Local` on the local date it reads the event (an hour passed twice
  → the later instant). Safe on Windows by construction: the CLI is the app's own child on that machine, both ask its one system zone; `TZ` is on the agent whitelist elsewhere. NOT VERIFIED on Windows.
- **(d) `a39ca4f`, RED on the base** (`d2c5b66`, again `89328a0`): no-launch `Assert.Single() Failure: The collection contained 2 items`; refused-before-work `Expected: 0 / Actual: 1.28`; last line `Expected: "You've
  hit your usage limit…" / Actual: "OpenAI Codex CLI did not finish: Reading "…`; cut-after-work `Actual: null` on the reason (its 1.28 green); typed-reason file `error CS0246: … 'UsageLimitPlan' could not be
  found`; two guards green. **Mutant**, the hold removed (`HeldFor` → null): no-launch test `Assert.Single() Failure: The collection contained 2 items`; restored → 14/14.
- **Deviations:** per runtime, not every launch (a harness role is billed elsewhere); a minute past the stated time (seconds dropped). Scan exclusions: `CancellationToken`, `_cts.Token`, `input_tokens`, `output_tokens`.
  **NOT done:** hold in memory (a restart forgets; next turn refused again at 0); the owner's chat neither records nor lifts it; the refused turn's wakes settle `failed`; no OpenCode/harness signature; AppHost wiring unrun; no Windows/CI.
